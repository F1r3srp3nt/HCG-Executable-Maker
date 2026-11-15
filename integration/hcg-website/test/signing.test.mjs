// Signing metadata (HCG Signing Vault, account step): tenant isolation, ERP App Access, refusal of anything secret,
// idempotent records, "remove HCG record" semantics and project preferences — through the HTTP handler and directly
// against the database (what a PostgREST request could reach).
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { createTestDatabase, LIFEOS_CLIENT } from './db.mjs';
import { handleExecutableMakerApiRequest } from '../src/lib/executable-maker/api.ts';

const BASE = 'https://beta.hakmicapitalgroup.com/api/public/executable-maker/v1';
let db, alice, bob, outsider;
const DEVICE_A = randomUUID();
const DEVICE_B = randomUUID();

async function call(token, path, { method = 'GET', body } = {}) {
  const headers = { Accept: 'application/json', 'X-ExecutableMaker-Contract': 'hcg.executable-maker/1' };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body) headers['Content-Type'] = 'application/json';
  const res = await handleExecutableMakerApiRequest(new Request(BASE + path, { method, headers, body: body ? JSON.stringify(body) : undefined }), db.rpc, db.storage);
  return { status: res.status, body: await res.json() };
}

const cert = (extra = {}) => ({
  assetType: 'certificate',
  sha1: 'A1B2C3D4E5F60718293A4B5C6D7E8F9012345678',
  sha256: 'AB'.repeat(32),
  displayName: 'Apple Distribution: Synthetic Test Team (ABCDE12345)',
  teamId: 'ABCDE12345',
  certificateKind: 'AppleDistribution',
  expiresAt: '2027-05-13T12:00:00Z',
  deviceId: DEVICE_A,
  deviceLabel: 'Studio Mac',
  ...extra,
});
const profile = (extra = {}) => ({
  assetType: 'profile', profileUuid: randomUUID(), displayName: 'Example App Store', teamId: 'ABCDE12345', profileKind: 'AppStore',
  bundleIdPattern: 'com.example.app', platforms: ['iOS'], expiresAt: '2027-05-13T12:00:00Z', deviceId: DEVICE_A, deviceLabel: 'Studio Mac', ...extra,
});

before(async () => {
  db = await createTestDatabase();
  alice = await db.createUser('alice@example.test');
  bob = await db.createUser('bob@example.test');
  outsider = await db.createUser('nobody@example.test');
  await db.grantERP(alice, 'executable-maker');
  await db.grantERP(bob, 'executable-maker');
});
after(async () => db.close());

test('records are per account: listing, deleting and reading another account\'s records is refused', async () => {
  const a = await call(db.token(alice), '/signing/assets', { method: 'POST', body: cert() });
  assert.equal(a.status, 200, JSON.stringify(a.body));
  assert.equal(a.body.asset.sha1, cert().sha1);
  assert.equal(a.body.asset.deviceLabel, 'Studio Mac');
  const b = await call(db.token(bob), '/signing/assets', { method: 'POST', body: cert({ deviceId: DEVICE_B }) });
  assert.equal(b.status, 200);

  const aliceList = await call(db.token(alice), '/signing/assets');
  assert.deepEqual(aliceList.body.assets.map((x) => x.id), [a.body.asset.id]);
  const bobList = await call(db.token(bob), '/signing/assets');
  assert.deepEqual(bobList.body.assets.map((x) => x.id), [b.body.asset.id]);

  // Bob cannot remove Alice's record (404: existence is not revealed), through the API or the database directly.
  assert.equal((await call(db.token(bob), `/signing/assets/${a.body.asset.id}`, { method: 'DELETE' })).status, 404);
  const bobClaims = db.tokens.get(db.token(bob));
  assert.equal((await db.rpcWithClaims('exm_signing_forget', { p_id: a.body.asset.id }, bobClaims)).status, 404);
  assert.equal((await call(db.token(alice), '/signing/assets')).body.assets.length, 1);

  // Tables are not reachable directly, not even one's own rows.
  const aliceClaims = db.tokens.get(db.token(alice));
  await assert.rejects(() => db.asAuthenticated('select * from exm.signing_assets', aliceClaims));
  await assert.rejects(() => db.asAuthenticated("insert into exm.signing_assets (owner_subject, asset_type, sha1, device_id) values (gen_random_uuid(), 'certificate', repeat('A', 40), gen_random_uuid())", aliceClaims));
  await assert.rejects(() => db.asAuthenticated('select * from exm.signing_project_preferences', aliceClaims));
});

test('only the desktop app with ERP App Access may use signing metadata', async () => {
  assert.equal((await call(db.token(outsider), '/signing/assets')).status, 403);
  assert.equal((await call(db.token(alice, LIFEOS_CLIENT), '/signing/assets')).status, 403);
  assert.equal((await call(db.token(alice, null), '/signing/assets')).status, 403);   // website session
  assert.equal((await call(null, '/signing/assets')).status, 401);
  const leaver = await db.createUser('leaver@example.test');
  await db.grantERP(leaver, 'executable-maker');
  const t = db.token(leaver);
  assert.equal((await call(t, '/signing/assets')).status, 200);
  await db.revokeERP(leaver, 'executable-maker');
  const r = await call(t, '/signing/assets');
  assert.equal(r.status, 403);
  assert.equal(r.body.error, 'revoked');
  await db.endSession(alice);
  const ended = await call(db.token(alice), '/signing/assets');
  assert.equal(ended.status, 401);
  alice = await (async () => { const u = await db.createUser('alice2@example.test'); await db.grantERP(u, 'executable-maker'); return u; })();
});

test('anything that is not public metadata is refused, not ignored', async () => {
  const t = db.token(bob);
  for (const extra of [{ privateKey: '-----BEGIN PRIVATE KEY-----' }, { p12: 'MIIK…' }, { password: 'hunter22' }, { provisionedDevices: ['00008110-000A1B2C3D4E5F60'] }]) {
    const r = await call(t, '/signing/assets', { method: 'POST', body: cert(extra) });
    assert.equal(r.status, 400, JSON.stringify(extra));
    assert.match(r.body.message, /never uploaded|not accepted/);
  }
  for (const bad of [cert({ sha1: 'not-hex' }), cert({ teamId: 'abc' }), cert({ deviceId: 'mac' }), cert({ certificateKind: 'Root' }),
                     profile({ profileUuid: 'x' }), profile({ platforms: ['Android'] }), cert({ profileUuid: randomUUID() }), { assetType: 'secret', deviceId: DEVICE_A }]) {
    assert.equal((await call(t, '/signing/assets', { method: 'POST', body: bad })).status, 400, JSON.stringify(bad));
  }
  assert.equal((await call(t, '/signing/assets', { method: 'POST', body: cert({ displayName: 'x'.repeat(5000) }) })).status, 413);
});

test('records are idempotent per fingerprint or profile UUID and device, and removal removes only HCG\'s record', async () => {
  const t = db.token(bob);
  const p = profile({ deviceId: DEVICE_B });
  const first = await call(t, '/signing/assets', { method: 'POST', body: p });
  assert.equal(first.status, 200, JSON.stringify(first.body));
  const again = await call(t, '/signing/assets', { method: 'POST', body: { ...p, displayName: 'Renamed', expiresAt: '2027-06-01T00:00:00Z' } });
  assert.equal(again.body.asset.id, first.body.asset.id);
  assert.equal(again.body.asset.displayName, 'Renamed');
  const sameCertAgain = await call(t, '/signing/assets', { method: 'POST', body: cert({ deviceId: DEVICE_B, displayName: 'Updated name' }) });
  const list = (await call(t, '/signing/assets')).body.assets;
  assert.equal(list.filter((x) => x.assetType === 'certificate').length, 1);
  assert.equal(list.find((x) => x.assetType === 'certificate').displayName, 'Updated name');
  // The same certificate recorded from a second Mac is a second record (that is how "on another Mac" is shown).
  await call(t, '/signing/assets', { method: 'POST', body: cert({ deviceId: randomUUID(), deviceLabel: 'Laptop' }) });
  assert.equal((await call(t, '/signing/assets')).body.assets.filter((x) => x.assetType === 'certificate').length, 2);

  const del = await call(t, `/signing/assets/${sameCertAgain.body.asset.id}`, { method: 'DELETE' });
  assert.deepEqual(del.body, { removed: true });
  assert.equal((await call(t, `/signing/assets/${sameCertAgain.body.asset.id}`, { method: 'DELETE' })).status, 404);
  assert.equal((await call(t, '/signing/assets/not-a-uuid', { method: 'DELETE' })).status, 404);
  const audit = await db.pool.query("select action from exm.audit where action like 'signing_%' and actor_subject = $1 order by id", [bob.id]);
  assert.ok(audit.rows.some((r) => r.action === 'signing_asset_forgotten'));
});

test('project preferences hold references only and are per account', async () => {
  const t = db.token(bob);
  const prefs = { platform: 'Ios', mode: 'Manual', route: 'IosAppStore', teamId: 'ABCDE12345', identitySha1: 'A1B2C3D4E5F60718293A4B5C6D7E8F9012345678',
                  profileUuid: randomUUID(), targets: [{ targetName: 'Widget', bundleIdentifier: 'com.example.app.widget', profileUuid: randomUUID() }] };
  const put = await call(t, '/signing/projects/com.example.app', { method: 'PUT', body: { preferences: prefs } });
  assert.equal(put.status, 200, JSON.stringify(put.body));
  assert.deepEqual((await call(t, '/signing/projects/com.example.app')).body.preferences, prefs);
  assert.equal((await call(db.token(alice), '/signing/projects/com.example.app')).body.preferences, null);

  for (const bad of [{ ...prefs, password: 'x' }, { ...prefs, teamId: 'nope' }, { ...prefs, route: 'Publish' },
                     { ...prefs, targets: [{ targetName: 'App', entitlements: { 'aps-environment': 'production' } }] },
                     { ...prefs, intendedDevices: ['00008110-000A1B2C3D4E5F60'] }]) {
    assert.equal((await call(t, '/signing/projects/com.example.app', { method: 'PUT', body: { preferences: bad } })).status, 400, JSON.stringify(bad));
  }
  assert.equal((await call(t, '/signing/projects/..%2Fetc', { method: 'PUT', body: { preferences: prefs } })).status, 400);
});
