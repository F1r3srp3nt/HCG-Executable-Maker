// Test database + PostgREST and Storage stand-ins. Each `rpc` call runs in its own transaction as role `authenticated`
// with `request.jwt.claims` set from the token — what PostgREST does after verifying a Supabase access token.
// SQLSTATE PTnnn -> HTTP nnn. The storage stand-in applies storage.objects RLS exactly as Supabase Storage does for a
// caller's token; signed uploads bypass RLS (as Supabase's do) but can only be created when the insert policy allows.
// Needs PostgreSQL at 127.0.0.1:5432 (user postgres, password EXM_TEST_PGPASSWORD, default 'exm-local-test').
import pg from 'pg';
import { readFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { crc32 } from 'node:zlib';

const here = new URL('.', import.meta.url);
const read = (rel) => readFileSync(new URL(rel, here), 'utf8');
const admin = { host: '127.0.0.1', port: 5432, user: 'postgres', password: process.env.EXM_TEST_PGPASSWORD ?? 'exm-local-test' };

export const EXM_CLIENT = '7e1c0d5a-2f1b-4c3e-9a8d-0b1c2d3e4f50'; // stands for Executable Maker's (future) registered client
export const LIFEOS_CLIENT = '50d941a4-946e-499a-9410-2ecdabb18a03';
export const STORAGE_HOST = 'https://qggdyzoydveafkmjgkpq.supabase.co';
const BUCKET = 'executable-maker-support';

export async function createTestDatabase({ erp = true } = {}) {
  const name = `exm_t_${randomUUID().replace(/-/g, '').slice(0, 12)}`;
  const root = new pg.Client({ ...admin, database: 'postgres' });
  await root.connect();
  await root.query(`create database ${name}`);
  await root.end();
  const pool = new pg.Pool({ ...admin, database: name, max: 8 });
  await pool.query(read('supabase-stub.sql'));
  if (erp) await pool.query(read('erp-stub.sql'));
  for (const migration of ['20261108200000_executable_maker_support.sql', '20261113230000_executable_maker_signing_metadata.sql']) {
    await pool.query(read(`../supabase/migrations/${migration}`));
  }
  await pool.query('insert into exm.clients (client_id, label) values ($1, $2)', [EXM_CLIENT, 'HCG Executable Maker (test)']);
  const tokens = new Map();
  const blobs = new Map();
  const uploadTokens = new Map();

  async function asCaller(claims, fn) {
    const c = await pool.connect();
    try {
      await c.query('begin');
      await c.query('set local role authenticated');
      await c.query("select set_config('request.jwt.claims', $1, true)", [JSON.stringify(claims)]);
      const result = await fn(c);
      await c.query('commit');
      return result;
    } catch (e) {
      await c.query('rollback').catch(() => {});
      throw e;
    } finally {
      c.release();
    }
  }
  const claimsOf = (token) => {
    const claims = tokens.get(token);
    if (!claims) throw Object.assign(new Error('JWT invalid'), { status: 401 });
    return claims;
  };

  const storage = {
    async signUpload(path, token) {
      // Supabase checks the insert policy when creating a signed upload URL: try the insert, then roll back.
      const claims = claimsOf(token);
      const c = await pool.connect();
      try {
        await c.query('begin');
        await c.query('set local role authenticated');
        await c.query("select set_config('request.jwt.claims', $1, true)", [JSON.stringify(claims)]);
        await c.query('insert into storage.objects (bucket_id, name, metadata) values ($1, $2, $3)', [BUCKET, path, { size: 0 }]);
      } finally {
        await c.query('rollback').catch(() => {});
        c.release();
      }
      const t = randomUUID();
      uploadTokens.set(t, path);
      return { url: `${STORAGE_HOST}/storage/v1/object/upload/sign/${BUCKET}/${path}?token=${t}`, expiresAt: new Date(Date.now() + 7200_000).toISOString() };
    },
    async signDownload(path, token, seconds) {
      const r = await asCaller(claimsOf(token), (c) => c.query('select name from storage.objects where bucket_id = $1 and name = $2', [BUCKET, path]));
      if (r.rowCount !== 1) throw Object.assign(new Error('Object not found'), { status: 404 });
      return { url: `${STORAGE_HOST}/storage/v1/object/sign/${BUCKET}/${path}?token=${randomUUID()}`, expiresAt: new Date(Date.now() + seconds * 1000).toISOString() };
    },
    async download(path, token) {
      const r = await asCaller(claimsOf(token), (c) => c.query('select name from storage.objects where bucket_id = $1 and name = $2', [BUCKET, path]));
      if (r.rowCount !== 1) throw Object.assign(new Error('Object not found'), { status: 404 });
      return blobs.get(path);
    },
    async remove(paths, token) {
      await asCaller(claimsOf(token), (c) => c.query('delete from storage.objects where bucket_id = $1 and name = any($2)', [BUCKET, paths]));
      for (const p of paths) {
        const still = await pool.query('select 1 from storage.objects where bucket_id = $1 and name = $2', [BUCKET, p]);
        if (still.rowCount === 0) blobs.delete(p);
      }
    },
  };

  const db = {
    name, pool, tokens, storage, blobs,
    async close() {
      await pool.end();
      const c = new pg.Client({ ...admin, database: 'postgres' });
      await c.connect();
      await c.query(`drop database if exists ${name} with (force)`);
      await c.end();
    },
    async createUser(email, meta = {}) {
      const id = randomUUID();
      const session = randomUUID();
      await pool.query('insert into auth.users (id, email, raw_user_meta_data) values ($1, $2, $3)', [id, email, meta]);
      await pool.query('insert into auth.sessions (id, user_id) values ($1, $2)', [session, id]);
      return { id, email, session };
    },
    /** A bearer token standing for a verified JWT. clientID null = a website session (the ERP). */
    token(user, clientID = EXM_CLIENT) {
      const token = `tok_${randomUUID()}`;
      const claims = { sub: user.id, role: 'authenticated', aud: 'authenticated', session_id: user.session, email: user.email };
      if (clientID) claims.client_id = clientID;
      tokens.set(token, claims);
      return token;
    },
    async endSession(user) { await pool.query('delete from auth.sessions where id = $1', [user.session]); },
    async grantERP(user, product, features = {}) {
      await pool.query(
        `insert into public.app_entitlements (employee_id, application_id, features)
         select $1, id, $3 from public.app_applications where product_id = $2
         on conflict (employee_id, application_id) do update set status = 'active', revoked_at = null, features = excluded.features`,
        [user.id, product, features]);
    },
    async revokeERP(user, product) {
      await pool.query(`update public.app_entitlements set status = 'revoked', revoked_at = now()
         where employee_id = $1 and application_id = (select id from public.app_applications where product_id = $2)`, [user.id, product]);
    },
    /** What the desktop app does with a signed upload URL: PUT the bytes (signed uploads bypass RLS). */
    async putSigned(url, bytes) {
      const t = new URL(url).searchParams.get('token');
      const path = uploadTokens.get(t);
      if (!path) throw new Error('invalid upload token');
      await pool.query('insert into storage.objects (bucket_id, name, metadata) values ($1, $2, $3) on conflict (bucket_id, name) do nothing',
        [BUCKET, path, { size: bytes.length, mimetype: 'application/zip' }]);
      blobs.set(path, bytes);
      return path;
    },
    async rpc(fn, args, token) {
      const claims = tokens.get(token);
      if (!claims) return { data: null, error: { code: 'PGRST301', message: 'JWT invalid' }, status: 401 };
      return db.rpcWithClaims(fn, args, claims);
    },
    async rpcWithClaims(fn, args, claims) {
      try {
        const res = await asCaller(claims, (c) => {
          const names = Object.keys(args);
          const sql = `select public.${fn}(${names.map((n, i) => `${n} => $${i + 1}`).join(', ')}) as r`;
          const values = names.map((n) => (args[n] !== null && typeof args[n] === 'object' ? JSON.stringify(args[n]) : args[n]));
          return c.query(sql, values);
        });
        return { data: res.rows[0].r, error: null, status: 200 };
      } catch (e) {
        const m = /^PT(\d{3})$/.exec(e.code ?? '');
        return { data: null, error: { code: e.code, message: e.message, details: e.detail ?? null }, status: m ? Number(m[1]) : e.code === '42501' ? 403 : 400 };
      }
    },
    async asAuthenticated(sql, claims) { return asCaller(claims, (c) => c.query(sql)).then((r) => r.rows); },
  };
  return db;
}

/** A minimal stored (uncompressed) ZIP. entries: [{name, data, mode?, size?, compressed?}] (mode 0o120777 = symlink). */
export function makeZip(entries) {
  const chunks = [];
  const central = [];
  let offset = 0;
  for (const e of entries) {
    const name = Buffer.from(e.name, 'utf8');
    const data = Buffer.from(e.data ?? '');
    const crc = crc32(data) >>> 0;
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18); local.writeUInt32LE(data.length, 22); local.writeUInt16LE(name.length, 26);
    chunks.push(local, name, data);
    const cd = Buffer.alloc(46);
    cd.writeUInt32LE(0x02014b50, 0); cd.writeUInt16LE(0x031e, 4); cd.writeUInt16LE(20, 6); cd.writeUInt32LE(crc, 16);
    cd.writeUInt32LE(e.compressed ?? data.length, 20); cd.writeUInt32LE(e.size ?? data.length, 24); cd.writeUInt16LE(name.length, 28);
    cd.writeUInt32LE(((e.mode ?? 0o100644) * 65536) >>> 0, 38); cd.writeUInt32LE(offset, 42);
    central.push(cd, name);
    offset += 30 + name.length + data.length;
  }
  const cdBuf = Buffer.concat(central);
  const eocd = Buffer.alloc(22);
  eocd.writeUInt32LE(0x06054b50, 0); eocd.writeUInt16LE(entries.length, 8); eocd.writeUInt16LE(entries.length, 10);
  eocd.writeUInt32LE(cdBuf.length, 12); eocd.writeUInt32LE(offset, 16);
  return new Uint8Array(Buffer.concat([...chunks, cdBuf, eocd]));
}
