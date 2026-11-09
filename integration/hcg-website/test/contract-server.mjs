// Contract server for HCG Executable Maker's cross-language test (C# client ↔ this handler ↔ the real migration on
// PostgreSQL). MOCK identity: tokens are opaque stand-ins for verified JWTs (no identity server involved); storage is the
// stand-in from db.mjs, with signed upload URLs served by this process.
//
// Usage: node --experimental-strip-types test/contract-server.mjs /tmp/exm-contract.json
// Writes {baseUrl, tokens:{granted, outsider, revoked, staffWebsite}, revokeUrl} to the given file, then serves until killed.
import { createServer } from 'node:http';
import { writeFileSync } from 'node:fs';
import { createTestDatabase } from './db.mjs';
import { handleExecutableMakerApiRequest } from '../src/lib/executable-maker/api.ts';

const out = process.argv[2] ?? '/tmp/exm-contract.json';
const db = await createTestDatabase();
const granted = await db.createUser('granted@example.test', { display_name: 'Granted User' });
const outsider = await db.createUser('outsider@example.test');
const revoked = await db.createUser('revoked@example.test');
const staff = await db.createUser('staff@example.test');
await db.grantERP(granted, 'executable-maker');
await db.grantERP(revoked, 'executable-maker');
await db.grantERP(staff, 'executable-maker-support-desk');
const tokens = { granted: db.token(granted), outsider: db.token(outsider), revoked: db.token(revoked), staffWebsite: db.token(staff, null) };

let base = '';
// Uploads are PUT to this server; rewrite the stand-in's storage URLs to point here.
const storage = {
  ...db.storage,
  async signUpload(path, token) {
    const s = await db.storage.signUpload(path, token);
    return { ...s, url: s.url.replace('https://qggdyzoydveafkmjgkpq.supabase.co', base) };
  },
};

const server = createServer(async (req, res) => {
  const chunks = [];
  for await (const c of req) chunks.push(c);
  const body = Buffer.concat(chunks);
  const url = new URL(req.url, base);
  if (req.method === 'PUT' && url.pathname.startsWith('/storage/v1/object/upload/sign/')) {
    try {
      await db.putSigned(url.toString().replace(base, 'https://qggdyzoydveafkmjgkpq.supabase.co'), new Uint8Array(body));
      res.writeHead(200, { 'Content-Type': 'application/json' }).end('{"Key":"ok"}');
    } catch {
      res.writeHead(400).end('{"error":"invalid upload"}');
    }
    return;
  }
  if (req.method === 'POST' && url.pathname === '/__test/revoke') {
    await db.revokeERP(revoked, 'executable-maker');
    res.writeHead(204).end();
    return;
  }
  const headers = new Headers();
  for (const [k, v] of Object.entries(req.headers)) if (typeof v === 'string') headers.set(k, v);
  const request = new Request(url, { method: req.method, headers, body: ['GET', 'HEAD'].includes(req.method) ? undefined : body });
  const response = await handleExecutableMakerApiRequest(request, db.rpc, storage);
  res.writeHead(response.status, Object.fromEntries(response.headers));
  res.end(Buffer.from(await response.arrayBuffer()));
});
server.listen(0, '127.0.0.1', () => {
  base = `http://127.0.0.1:${server.address().port}`;
  writeFileSync(out, JSON.stringify({ baseUrl: base, apiPath: '/api/public/executable-maker/v1', tokens, revokeUrl: `${base}/__test/revoke`, subjects: { granted: granted.id } }));
  console.log(`contract server on ${base}`);
});
const stop = async () => { server.close(); await db.close(); process.exit(0); };
process.on('SIGTERM', stop);
process.on('SIGINT', stop);
