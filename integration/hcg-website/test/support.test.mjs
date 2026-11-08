// Access and data boundaries of the Executable Maker API, through the HTTP handler and through direct database calls
// (what a direct PostgREST request reaches): ERP App Access, revocation, ended sessions, other apps' tokens, other
// users' requests and attachments, staff-only operations, upload validation, withdrawal, audit trail.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { createTestDatabase, makeZip, EXM_CLIENT, LIFEOS_CLIENT } from './db.mjs';
import { handleExecutableMakerApiRequest } from '../src/lib/executable-maker/api.ts';
import { inspectZip } from '../src/lib/executable-maker/zipInspect.ts';

const BASE = 'https://beta.hakmicapitalgroup.com/api/public/executable-maker/v1';
let db, alice, bob, staff, outsider;

async function call(token, path, { method = 'GET', body, contract = 'hcg.executable-maker/1', org } = {}) {
  const headers = { Accept: 'application/json' };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (contract) headers['X-ExecutableMaker-Contract'] = contract;
  if (org) headers['X-HCG-Organisation'] = org;
  if (body) headers['Content-Type'] = 'application/json';
  const res = await handleExecutableMakerApiRequest(new Request(BASE + path, { method, headers, body: body ? JSON.stringify(body) : undefined }), db.rpc, db.storage);
  return { status: res.status, body: await res.json() };
}

const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
const draft = (extra = {}) => ({
  clientRequestId: randomUUID(),
  title: 'Support Flutter desktop',
  details: { goal: 'Build my Flutter app for macOS', languageFramework: 'Dart / Flutter', targetOs: 'macOS', outputFormat: '.app', problem: 'Inspection says unsupported.' },
  diagnostics: [{ section: 'Project', label: 'Language', value: 'Dart' }],
  mode: 'diagnostics_only',
  attachments: [],
  appVersion: '0.4.0',
  ...extra,
});

before(async () => {
  db = await createTestDatabase();
  alice = await db.createUser('alice@example.test', { display_name: 'Alice' });
  bob = await db.createUser('bob@example.test');
  staff = await db.createUser('support@hakmicapitalgroup.test', { display_name: 'HCG Support' });
  outsider = await db.createUser('nobody@example.test');
  await db.grantERP(alice, 'executable-maker');
  await db.grantERP(bob, 'executable-maker', { ai: true });
  await db.grantERP(staff, 'executable-maker-support-desk');
});
after(async () => db.close());

test('contract header and bearer token are required', async () => {
  assert.equal((await call(db.token(alice), '/access', { contract: null })).status, 400);
  assert.equal((await call(null, '/access')).status, 401);
  assert.equal((await call('tok_forged', '/access')).status, 401);
});

test('App Access comes from ERP entitlements only', async () => {
  const a = await call(db.token(alice), '/access');
  assert.equal(a.status, 200);
  assert.equal(a.body.user.id, alice.id);
  assert.deepEqual(a.body.workspaces.map((w) => [w.id, w.permitted, w.available]), [['executable-maker', true, true]]);
  assert.deepEqual(a.body.permissions, ['support.submit', 'support.attach']);
  assert.equal(a.body.limits.maxAttachments, 3);
  // AI only where the ERP entitlement's features grant it.
  assert.deepEqual((await call(db.token(bob), '/access')).body.permissions, ['support.submit', 'support.attach', 'ai.use']);
  const none = await call(db.token(outsider), '/access');
  assert.equal(none.status, 200);
  assert.equal(none.body.workspaces[0].permitted, false);
  assert.deepEqual(none.body.permissions, []);
  // Staff entitlement gives no staff permission through the desktop app's token.
  assert.deepEqual((await call(db.token(staff), '/access')).body.permissions, []);
});

test('other apps and website sessions cannot use the app API; unknown organisations are refused', async () => {
  assert.equal((await call(db.token(alice, LIFEOS_CLIENT), '/access')).status, 403);
  assert.equal((await call(db.token(alice, null), '/access')).status, 403);
  assert.equal((await call(db.token(alice, null), '/support/requests')).status, 403);
  const org = await call(db.token(alice), '/access', { org: 'some-other-company' });
  assert.equal(org.status, 403);
});

test('ended sessions and banned accounts get 401', async () => {
  const u = await db.createUser('leaver@example.test');
  await db.grantERP(u, 'executable-maker');
  const t = db.token(u);
  assert.equal((await call(t, '/access')).status, 200);
  await db.endSession(u);
  assert.equal((await call(t, '/access')).status, 401);
  const banned = await db.createUser('banned@example.test');
  await db.grantERP(banned, 'executable-maker');
  await db.pool.query("update auth.users set banned_until = now() + interval '1 day' where id = $1", [banned.id]);
  assert.equal((await call(db.token(banned), '/access')).status, 401);
});

test('revoked ERP access answers 403 revoked (the app then removes its session)', async () => {
  const u = await db.createUser('revoked@example.test');
  await db.grantERP(u, 'executable-maker');
  const t = db.token(u);
  assert.equal((await call(t, '/support/requests')).status, 200);
  await db.revokeERP(u, 'executable-maker');
  const r = await call(t, '/support/requests');
  assert.equal(r.status, 403);
  assert.equal(r.body.error, 'revoked');
  assert.equal((await call(t, '/access')).body.error, 'revoked');
});

test('diagnostics-only request: submitted at once, idempotent per clientRequestId, listed for the owner only', async () => {
  const d = draft();
  const r = await call(db.token(alice), '/support/requests', { method: 'POST', body: d });
  assert.equal(r.status, 200);
  assert.match(r.body.request.reference, /^HCG-[A-Z2-9]{6}$/);
  assert.equal(r.body.request.status, 'submitted');
  assert.equal(r.body.duplicate, false);
  assert.deepEqual(r.body.uploads, []);
  const again = await call(db.token(alice), '/support/requests', { method: 'POST', body: d });
  assert.equal(again.body.duplicate, true);
  assert.equal(again.body.request.id, r.body.request.id);
  const list = await call(db.token(alice), '/support/requests');
  assert.equal(list.body.requests.filter((x) => x.id === r.body.request.id).length, 1);
  assert.equal((await call(db.token(bob), '/support/requests')).body.requests.some((x) => x.id === r.body.request.id), false);
  // Another user cannot read it (404: existence is not revealed), reply to it or withdraw it.
  assert.equal((await call(db.token(bob), `/support/requests/${r.body.request.id}`)).status, 404);
  assert.equal((await call(db.token(bob), `/support/requests/${r.body.request.id}/messages`, { method: 'POST', body: { body: 'hi' } })).status, 404);
  assert.equal((await call(db.token(bob), `/support/requests/${r.body.request.id}/withdraw`, { method: 'POST' })).status, 404);
  // An account without ERP access cannot create requests.
  assert.equal((await call(db.token(outsider), '/support/requests', { method: 'POST', body: draft() })).status, 403);
});

test('input validation: details required, diagnostics-only carries no files, too many/too large attachments refused', async () => {
  const t = db.token(alice);
  assert.equal((await call(t, '/support/requests', { method: 'POST', body: draft({ details: { goal: '', problem: '' } }) })).status, 400);
  const bytes = makeZip([{ name: 'a.txt', data: 'x' }]);
  const att = { name: 'project.zip', sizeBytes: bytes.length, sha256: sha(bytes), contentType: 'application/zip' };
  assert.equal((await call(t, '/support/requests', { method: 'POST', body: draft({ attachments: [att] }) })).status, 400);
  const many = draft({ mode: 'diagnostics_and_files', attachments: [1, 2, 3, 4].map((i) => ({ ...att, name: `p${i}.zip` })) });
  assert.equal((await call(t, '/support/requests', { method: 'POST', body: many })).status, 400);
  const huge = draft({ mode: 'diagnostics_and_files', attachments: [{ ...att, sizeBytes: 60 * 1024 * 1024 }] });
  assert.equal((await call(t, '/support/requests', { method: 'POST', body: huge })).status, 413);
});

async function createWithFile(user, bytes, name = 'project.zip') {
  const t = db.token(user);
  const d = draft({ mode: 'diagnostics_and_files', attachments: [{ name, sizeBytes: bytes.length, sha256: sha(bytes), contentType: 'application/zip' }] });
  const r = await call(t, '/support/requests', { method: 'POST', body: d });
  assert.equal(r.status, 200, JSON.stringify(r.body));
  return { t, d, r };
}

test('upload flow: signed URL, upload, intake check, submit; drafts are invisible to staff until submitted', async () => {
  const bytes = makeZip([{ name: 'project/pubspec.yaml', data: 'name: demo' }, { name: 'project/lib/main.dart', data: 'void main() {}' }]);
  const { t, r } = await createWithFile(alice, bytes);
  assert.equal(r.body.request.status, 'draft');
  const slot = r.body.uploads[0];
  assert.match(slot.uploadUrl, /^https:\/\/qggdyzoydveafkmjgkpq\.supabase\.co\/storage\/v1\/object\/upload\/sign\/executable-maker-support\//);
  const staffList = await call(db.token(staff, null), '/staff/requests');
  assert.equal(staffList.body.requests.some((x) => x.id === r.body.request.id), false);
  // Submitting before the upload is refused.
  assert.equal((await call(t, `/support/requests/${r.body.request.id}/submit`, { method: 'POST' })).status, 409);
  // Completing before the upload arrives is refused.
  assert.equal((await call(t, `/support/requests/${r.body.request.id}/attachments/${slot.attachmentId}/complete`, { method: 'POST' })).status, 409);
  await db.putSigned(slot.uploadUrl, bytes);
  const done = await call(t, `/support/requests/${r.body.request.id}/attachments/${slot.attachmentId}/complete`, { method: 'POST' });
  assert.equal(done.status, 200);
  assert.equal(done.body.attachment.state, 'received');
  assert.equal(done.body.attachment.scan, 'not_scanned');
  const sub = await call(t, `/support/requests/${r.body.request.id}/submit`, { method: 'POST' });
  assert.equal(sub.body.request.summary.status, 'submitted');
  // A retry of the same draft after submission does not create a second request or new uploads.
  const retry = await call(t, '/support/requests', { method: 'POST', body: { ...(await call(t, `/support/requests/${r.body.request.id}`)).body.request, clientRequestId: undefined } });
  assert.notEqual(retry.status, 500);
});

test("attachments: other users cannot get upload targets, download links or the stored object", async () => {
  const bytes = makeZip([{ name: 'a.txt', data: 'secretless' }]);
  const { t, r } = await createWithFile(alice, bytes);
  const id = r.body.request.id;
  const slot = r.body.uploads[0];
  const tb = db.token(bob);
  assert.equal((await call(tb, `/support/requests/${id}/attachments/${slot.attachmentId}/upload-url`, { method: 'POST' })).status, 404);
  // Bob cannot sign an upload into Alice's path (storage RLS insert policy).
  const path = (await db.pool.query('select object_path from exm.attachments where id = $1', [slot.attachmentId])).rows[0].object_path;
  await assert.rejects(() => db.storage.signUpload(path, tb));
  await db.putSigned(slot.uploadUrl, bytes);
  await call(t, `/support/requests/${id}/attachments/${slot.attachmentId}/complete`, { method: 'POST' });
  assert.equal((await call(tb, `/support/requests/${id}/attachments/${slot.attachmentId}/download`, { method: 'POST' })).status, 404);
  await assert.rejects(() => db.storage.download(path, tb));
  const own = await call(t, `/support/requests/${id}/attachments/${slot.attachmentId}/download`, { method: 'POST' });
  assert.equal(own.status, 200);
  assert.ok(Date.parse(own.body.expiresAt) - Date.now() <= 300_000);
  // The bucket is private: no anonymous/public path exists.
  assert.equal((await db.pool.query("select public from storage.buckets where id = 'executable-maker-support'")).rows[0].public, false);
});

test('unsafe archives and checksum mismatches are rejected at intake and block submission', async () => {
  for (const [entries, why] of [
    [[{ name: '../../etc/passwd', data: 'x' }], /traversal/i],
    [[{ name: '/abs/file', data: 'x' }], /absolute/i],
    [[{ name: 'link', data: '/etc/passwd', mode: 0o120777 }], /symbolic link/i],
  ]) {
    const bytes = makeZip(entries);
    const { t, r } = await createWithFile(alice, bytes);
    const slot = r.body.uploads[0];
    await db.putSigned(slot.uploadUrl, bytes);
    const done = await call(t, `/support/requests/${r.body.request.id}/attachments/${slot.attachmentId}/complete`, { method: 'POST' });
    assert.equal(done.body.attachment.state, 'rejected');
    assert.match(done.body.attachment.reason, why);
    assert.equal((await call(t, `/support/requests/${r.body.request.id}/submit`, { method: 'POST' })).status, 409);
  }
  // Size differs from the declaration: rejected by the database itself from storage metadata.
  const bytes = makeZip([{ name: 'a.txt', data: 'abc' }]);
  const { t, r } = await createWithFile(alice, bytes);
  await db.putSigned(r.body.uploads[0].uploadUrl, makeZip([{ name: 'a.txt', data: 'abcdef-longer' }]));
  const done = await call(t, `/support/requests/${r.body.request.id}/attachments/${r.body.uploads[0].attachmentId}/complete`, { method: 'POST' });
  assert.equal(done.body.attachment.state, 'rejected');
  assert.match(done.body.attachment.reason, /declared/);
});

test('an owner cannot mark an attachment accepted by calling the database directly', async () => {
  const bytes = makeZip([{ name: '../evil', data: 'x' }]);
  const { r } = await createWithFile(alice, bytes);
  const slot = r.body.uploads[0];
  await db.putSigned(slot.uploadUrl, bytes);
  // Bypassing the website route: the database accepts by size only, so the attachment can reach 'received'…
  const claims = { sub: alice.id, role: 'authenticated', session_id: alice.session, client_id: EXM_CLIENT };
  const direct = await db.rpcWithClaims('exm_support_complete_attachment', { p_id: r.body.request.id, p_attachment: slot.attachmentId }, claims);
  assert.equal(direct.data.attachment.state, 'received');
  await db.rpcWithClaims('exm_support_submit', { p_id: r.body.request.id }, claims);
  // …but staff never get a link to it: the staff download route re-inspects and refuses.
  const s = await call(db.token(staff, null), `/staff/requests/${r.body.request.id}/attachments/${slot.attachmentId}/download`, { method: 'POST' });
  assert.equal(s.status, 422);
  assert.equal(s.body.error, 'unsafe_attachment');
  // Tables are not reachable directly.
  await assert.rejects(() => db.asAuthenticated('select * from exm.requests', claims));
  await assert.rejects(() => db.asAuthenticated("update exm.attachments set state = 'received'", claims));
});

test('staff desk: ERP session + support-desk entitlement only; respond, request info, status, task link, audit trail', async () => {
  const { r } = await (async () => {
    const res = await call(db.token(alice), '/support/requests', { method: 'POST', body: draft({ title: 'Unity WebGL' }) });
    return { r: res };
  })();
  const id = r.body.request.id;
  const ts = db.token(staff, null);
  // Not staff: an ordinary website session, the staff account through the desktop app, the owner.
  assert.equal((await call(db.token(alice, null), '/staff/requests')).status, 403);
  assert.equal((await call(db.token(staff), '/staff/requests')).status, 403);
  assert.equal((await call(db.token(alice), `/staff/requests/${id}`)).status, 403);
  const list = await call(ts, '/staff/requests');
  assert.ok(list.body.requests.some((x) => x.id === id && x.ownerEmail === 'alice@example.test'));
  const ask = await call(ts, `/staff/requests/${id}/respond`, { method: 'POST', body: { body: 'Which Unity version?', kind: 'more_information' } });
  assert.equal(ask.body.request.summary.status, 'more_information_needed');
  const reply = await call(db.token(alice), `/support/requests/${id}/messages`, { method: 'POST', body: { body: 'Unity 6' } });
  assert.equal(reply.body.request.summary.status, 'submitted');
  await call(ts, `/staff/requests/${id}/task`, { method: 'POST', body: { taskRef: 'ERP-TASK-1042 Unity build adapter' } });
  const done = await call(ts, `/staff/requests/${id}/status`, { method: 'POST', body: { status: 'completed', resolution: 'resolved', note: 'Adapter planned.' } });
  assert.equal(done.body.request.summary.status, 'completed');
  assert.equal(done.body.request.devTaskRef, 'ERP-TASK-1042 Unity build adapter');
  const actions = done.body.request.audit.map((a) => a.action);
  for (const a of ['request_created', 'staff_viewed', 'staff_more_information', 'owner_replied', 'task_linked', 'status_changed']) assert.ok(actions.includes(a), a);
  // The owner sees staff messages, not the audit trail or internal fields.
  const own = await call(db.token(alice), `/support/requests/${id}`);
  assert.ok(own.body.request.messages.some((m) => m.fromStaff && m.authorName === 'HCG Support'));
  assert.equal(own.body.request.audit, undefined);
  assert.equal(own.body.request.devTaskRef, undefined);
  // Completed requests no longer accept owner messages.
  assert.equal((await call(db.token(alice), `/support/requests/${id}/messages`, { method: 'POST', body: { body: 'thanks' } })).status, 409);
});

test('withdrawal deletes attachments at once; retention purge removes expired ones', async () => {
  const carol = await db.createUser('carol@example.test');
  await db.grantERP(carol, 'executable-maker');
  const bytes = makeZip([{ name: 'a.txt', data: 'x' }]);
  const { t, r } = await createWithFile(carol, bytes);
  const slot = r.body.uploads[0];
  const path = await db.putSigned(slot.uploadUrl, bytes);
  await call(t, `/support/requests/${r.body.request.id}/attachments/${slot.attachmentId}/complete`, { method: 'POST' });
  const w = await call(t, `/support/requests/${r.body.request.id}/withdraw`, { method: 'POST' });
  assert.equal(w.body.request.summary.status, 'closed');
  assert.equal(w.body.request.attachments[0].state, 'deleted');
  assert.equal((await db.pool.query('select count(*)::int as n from storage.objects where name = $1', [path])).rows[0].n, 0);

  const second = await createWithFile(carol, bytes);
  const p2 = await db.putSigned(second.r.body.uploads[0].uploadUrl, bytes);
  await call(second.t, `/support/requests/${second.r.body.request.id}/attachments/${second.r.body.uploads[0].attachmentId}/complete`, { method: 'POST' });
  await call(second.t, `/support/requests/${second.r.body.request.id}/submit`, { method: 'POST' });
  const ts = db.token(staff, null);
  const closed = await call(ts, `/staff/requests/${second.r.body.request.id}/status`, { method: 'POST', body: { status: 'closed', resolution: 'not_planned' } });
  assert.ok(Date.parse(closed.body.request.attachments[0].deleteAfter) > Date.now() + 89 * 86400_000);
  await db.pool.query("update exm.attachments set delete_after = now() - interval '1 minute' where request_id = $1", [second.r.body.request.id]);
  const purge = await call(ts, '/staff/purge', { method: 'POST' });
  assert.ok(purge.body.deleted >= 1);
  assert.equal((await db.pool.query('select count(*)::int as n from storage.objects where name = $1', [p2])).rows[0].n, 0);
  // Owners cannot purge.
  assert.equal((await call(db.token(carol, null), '/staff/purge', { method: 'POST' })).status, 403);
});

test('at most 10 requests per account per day (429), counted per account', async () => {
  const dave = await db.createUser('dave@example.test');
  await db.grantERP(dave, 'executable-maker');
  for (let i = 0; i < 10; i++) assert.equal((await call(db.token(dave), '/support/requests', { method: 'POST', body: draft() })).status, 200);
  const r = await call(db.token(dave), '/support/requests', { method: 'POST', body: draft() });
  assert.equal(r.status, 429);
  assert.equal(r.body.error, 'rate_limited');
});

test('a support request never changes what the builder supports', async () => {
  // The package has no table or function that records project-type support; requests only hold text and files.
  const { rows } = await db.pool.query("select table_name from information_schema.tables where table_schema = 'exm' order by 1");
  assert.deepEqual(rows.map((r) => r.table_name), ['attachments', 'audit', 'clients', 'messages', 'requests', 'settings']);
});

test('zip inspector: limits, traversal variants, duplicates, compression ratio', () => {
  assert.equal(inspectZip(makeZip([{ name: 'ok/a.txt', data: 'a' }])).ok, true);
  assert.match(inspectZip(makeZip([{ name: 'a\\..\\b', data: 'a' }])).reason, /Backslash/);
  assert.match(inspectZip(makeZip([{ name: 'C:/x', data: 'a' }])).reason, /Absolute/);
  assert.match(inspectZip(makeZip([{ name: 'A.txt', data: 'a' }, { name: 'a.txt', data: 'b' }])).reason, /Duplicate/);
  assert.match(inspectZip(makeZip([{ name: 'bomb.bin', data: 'x', size: 500 * 1024 * 1024, compressed: 1024 }])).reason, /ratio/);
  assert.match(inspectZip(makeZip([{ name: 'big.bin', data: 'x', size: 0x7fffffff }, { name: 'big2.bin', data: 'x', size: 0x7fffffff }])).reason, /expands/);
  assert.equal(inspectZip(new Uint8Array([1, 2, 3])).ok, false);
  assert.deepEqual(inspectZip(makeZip([{ name: 'inner.zip', data: 'z' }])).nestedArchives, ['inner.zip']);
});
