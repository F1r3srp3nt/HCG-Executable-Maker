// HCG Executable Maker API — `hcg.executable-maker/1` (server side, HCG website).
//
// Framework-agnostic: takes a Fetch API Request, returns a Response. The TanStack Start route
// `src/routes/api/public/executable-maker/v1/$.ts` forwards to it. Every authorization decision runs in the database
// (supabase/migrations/20261108200000_executable_maker_support.sql) from the caller's own access token; this layer
// routes, checks the contract header, signs storage URLs *as the caller* (storage RLS applies) and inspects uploaded
// archives. It holds no service-role key and never decides access itself.
//
// Base /api/public/executable-maker/v1. App routes (the desktop app's OAuth token):
//   GET  /access
//   GET  /support/requests                              POST /support/requests
//   GET  /support/requests/{id}
//   POST /support/requests/{id}/attachments/{aid}/upload-url | complete | download
//   POST /support/requests/{id}/submit | messages | withdraw
// Staff routes (ERP website session, ERP App Access product executable-maker-support-desk):
//   GET  /staff/requests?status=        GET /staff/requests/{id}
//   POST /staff/requests/{id}/respond | status | task
//   POST /staff/requests/{id}/attachments/{aid}/download
//   POST /staff/purge

import { DEFAULT_ZIP_LIMITS, inspectZip, isZip, type ZipReport } from './zipInspect.ts';

export const EXM_CONTRACT = 'hcg.executable-maker/1';
export const EXM_CONTRACT_HEADER = 'X-ExecutableMaker-Contract';
export const EXM_ORGANISATION_HEADER = 'X-HCG-Organisation';
export const EXM_API_PREFIX = '/api/public/executable-maker/v1';
const MAX_JSON_BYTES = 512 * 1024;
const DOWNLOAD_SECONDS = 300;

export type RpcError = { code?: string | null; message?: string | null; details?: string | null };
export type RpcResult = { data: unknown; error: RpcError | null; status?: number };
/** Calls a Postgres function through PostgREST *as the caller* (their bearer token). */
export type Rpc = (fn: string, args: Record<string, unknown>, accessToken: string) => Promise<RpcResult>;

/** Supabase Storage, called with the caller's token so the bucket's RLS policies apply. */
export interface SupportStorage {
  signUpload(path: string, accessToken: string): Promise<{ url: string; expiresAt: string }>;
  signDownload(path: string, accessToken: string, seconds: number): Promise<{ url: string; expiresAt: string }>;
  download(path: string, accessToken: string): Promise<Uint8Array>;
  remove(paths: string[], accessToken: string): Promise<void>;
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', [EXM_CONTRACT_HEADER]: EXM_CONTRACT },
  });
}
const error = (status: number, code: string, message?: string) => json(status, message ? { error: code, message } : { error: code });

export function mapRpcError(result: RpcResult): Response {
  const code = result.error?.code ?? '';
  const custom = /^PT(\d{3})$/.exec(code);
  if (custom) return error(Number(custom[1]), result.error?.message || 'error', result.error?.details || undefined);
  if (result.status === 401 || code.startsWith('PGRST30')) return error(401, 'unauthorized', 'Missing, invalid or expired session.');
  if (code === '42501' || result.status === 403) return error(403, 'forbidden', 'Not allowed.');
  if (result.status === 429) return error(429, 'rate_limited', 'Too many requests. Wait a minute and try again.');
  return error(503, 'unavailable', 'HCG Executable Maker services are temporarily unavailable.');
}

function bearer(request: Request): string | null {
  const m = /^Bearer\s+([A-Za-z0-9._~+/=-]+)$/.exec((request.headers.get('Authorization') ?? '').trim());
  return m ? m[1] : null;
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

async function readJson(request: Request): Promise<Record<string, unknown> | Response> {
  const length = Number(request.headers.get('Content-Length') ?? '0');
  if (length > MAX_JSON_BYTES) return error(413, 'too_large', 'The request is too large.');
  const text = await request.text();
  if (text.length > MAX_JSON_BYTES) return error(413, 'too_large', 'The request is too large.');
  if (text.trim() === '') return {};
  try {
    const body = JSON.parse(text);
    return body && typeof body === 'object' && !Array.isArray(body) ? body : error(400, 'invalid', 'The body must be a JSON object.');
  } catch {
    return error(400, 'invalid', 'The body is not JSON.');
  }
}

async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', bytes);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

/** Checksum + (for ZIPs) structure. Used at intake (owner) and again before any staff download. */
export async function inspectUpload(bytes: Uint8Array, declaredSha256: string, contentType: string): Promise<{ ok: boolean; reason?: string; zip?: ZipReport }> {
  if ((await sha256Hex(bytes)) !== declaredSha256.toLowerCase()) return { ok: false, reason: 'The stored file does not match its declared SHA-256 checksum.' };
  if (contentType === 'application/zip' || isZip(bytes)) {
    const zip = inspectZip(bytes, DEFAULT_ZIP_LIMITS);
    return zip.ok ? { ok: true, zip } : { ok: false, reason: zip.reason, zip };
  }
  return { ok: true };
}

type Ctx = { request: Request; url: URL; token: string; rpc: Rpc; storage: SupportStorage };

async function call(ctx: Ctx, fn: string, args: Record<string, unknown> = {}): Promise<{ data: any } | Response> {
  let result: RpcResult;
  try {
    result = await ctx.rpc(fn, args, ctx.token);
  } catch {
    return error(503, 'unavailable', 'HCG Executable Maker services are temporarily unavailable.');
  }
  return result.error ? mapRpcError(result) : { data: result.data as any };
}

async function signUploads(ctx: Ctx, uploads: any[]): Promise<any[]> {
  const out = [];
  for (const u of uploads) {
    const signed = await ctx.storage.signUpload(u.path, ctx.token);
    out.push({ attachmentId: u.attachmentId, name: u.name, uploadUrl: signed.url, expiresAt: signed.expiresAt, maxBytes: Number(u.maxBytes) });
  }
  return out;
}

export async function handleExecutableMakerApiRequest(request: Request, rpc: Rpc, storage: SupportStorage): Promise<Response> {
  const url = new URL(request.url);
  const index = url.pathname.indexOf(EXM_API_PREFIX);
  if (index < 0) return error(404, 'not_found');
  const parts = url.pathname.slice(index + EXM_API_PREFIX.length).split('/').filter(Boolean).map(decodeURIComponent);
  if (request.headers.get(EXM_CONTRACT_HEADER) !== EXM_CONTRACT) {
    return error(400, 'unsupported_contract', `Send ${EXM_CONTRACT_HEADER}: ${EXM_CONTRACT}.`);
  }
  const token = bearer(request);
  if (!token) return error(401, 'unauthorized', 'Missing, invalid or expired session.');
  const ctx: Ctx = { request, url, token, rpc, storage };
  const m = request.method;
  const org = request.headers.get(EXM_ORGANISATION_HEADER) || null;
  if (parts.some((p, i) => i > 0 && /^(requests|attachments)$/.test(parts[i - 1]) && !UUID.test(p))) return error(404, 'not_found');

  const unwrap = async (r: { data: any } | Response, map?: (d: any) => unknown | Promise<unknown>) =>
    r instanceof Response ? r : json(200, map ? await map(r.data) : r.data);

  try {
    // ---- App ----
    if (m === 'GET' && parts.join('/') === 'access') return unwrap(await call(ctx, 'exm_access_directory', { p_organisation: org }));
    if (parts[0] === 'support' && parts[1] === 'requests') {
      const [, , id, sub, aid, action] = parts;
      if (!id) {
        if (m === 'GET') return unwrap(await call(ctx, 'exm_support_list'));
        if (m === 'POST') {
          const body = await readJson(request);
          if (body instanceof Response) return body;
          const r = await call(ctx, 'exm_support_create', { p_request: body, p_organisation: org });
          return unwrap(r, async (d) => ({ request: d.request, duplicate: d.duplicate, uploads: await signUploads(ctx, d.uploads ?? []) }));
        }
      }
      if (id && !sub && m === 'GET') return unwrap(await call(ctx, 'exm_support_get', { p_id: id }));
      if (id && m === 'POST' && sub === 'submit') return unwrap(await call(ctx, 'exm_support_submit', { p_id: id }));
      if (id && m === 'POST' && sub === 'messages') {
        const body = await readJson(request);
        if (body instanceof Response) return body;
        return unwrap(await call(ctx, 'exm_support_reply', { p_id: id, p_body: String(body.body ?? '') }));
      }
      if (id && m === 'POST' && sub === 'withdraw') {
        const r = await call(ctx, 'exm_support_withdraw', { p_id: id });
        if (r instanceof Response) return r;
        if (r.data.deletePaths?.length) await storage.remove(r.data.deletePaths, token);
        return json(200, { request: r.data.request });
      }
      if (id && m === 'POST' && sub === 'attachments' && aid && action === 'upload-url') {
        const r = await call(ctx, 'exm_support_upload_target', { p_id: id, p_attachment: aid });
        return unwrap(r, async (d) => ({ upload: (await signUploads(ctx, [d]))[0] }));
      }
      if (id && m === 'POST' && sub === 'attachments' && aid && action === 'complete') {
        const r = await call(ctx, 'exm_support_complete_attachment', { p_id: id, p_attachment: aid });
        if (r instanceof Response || r.data.attachment.state !== 'received') return unwrap(r);
        // Intake check: checksum and archive structure of what was actually stored.
        const target = await call(ctx, 'exm_support_download_target', { p_id: id, p_attachment: aid });
        if (target instanceof Response) return target;
        const bytes = await storage.download(target.data.path, token);
        const check = await inspectUpload(bytes, target.data.sha256, target.data.contentType);
        if (!check.ok) return unwrap(await call(ctx, 'exm_support_reject_attachment', { p_id: id, p_attachment: aid, p_reason: check.reason }));
        return json(200, r.data);
      }
      if (id && m === 'POST' && sub === 'attachments' && aid && action === 'download') {
        const r = await call(ctx, 'exm_support_download_target', { p_id: id, p_attachment: aid });
        if (r instanceof Response) return r;
        const signed = await storage.signDownload(r.data.path, token, DOWNLOAD_SECONDS);
        return json(200, { url: signed.url, expiresAt: signed.expiresAt });
      }
    }

    // ---- Staff (ERP) ----
    if (parts[0] === 'staff') {
      if (m === 'POST' && parts[1] === 'purge') {
        const r = await call(ctx, 'exm_staff_purge_expired');
        if (r instanceof Response) return r;
        if (r.data.deletePaths?.length) await storage.remove(r.data.deletePaths, token);
        return json(200, { deleted: r.data.deletePaths?.length ?? 0 });
      }
      if (parts[1] === 'requests') {
        const [, , id, sub, aid, action] = parts;
        if (!id && m === 'GET') return unwrap(await call(ctx, 'exm_staff_list', { p_status: url.searchParams.get('status') }));
        if (id && !sub && m === 'GET') return unwrap(await call(ctx, 'exm_staff_get', { p_id: id }));
        if (id && m === 'POST' && ['respond', 'status', 'task'].includes(sub)) {
          const body = await readJson(request);
          if (body instanceof Response) return body;
          if (sub === 'respond') return unwrap(await call(ctx, 'exm_staff_respond', { p_id: id, p_body: String(body.body ?? ''), p_kind: String(body.kind ?? 'response') }));
          if (sub === 'status') return unwrap(await call(ctx, 'exm_staff_set_status', { p_id: id, p_status: String(body.status ?? ''), p_resolution: body.resolution ?? null, p_note: body.note ?? null }));
          return unwrap(await call(ctx, 'exm_staff_link_task', { p_id: id, p_task_ref: body.taskRef ?? null }));
        }
        if (id && m === 'POST' && sub === 'attachments' && aid && action === 'download') {
          const r = await call(ctx, 'exm_staff_attachment_target', { p_id: id, p_attachment: aid });
          if (r instanceof Response) return r;
          // Re-inspect before handing out a link: the owner could have bypassed the intake route.
          const bytes = await storage.download(r.data.path, token);
          const check = await inspectUpload(bytes, r.data.sha256, r.data.contentType);
          if (!check.ok) return error(422, 'unsafe_attachment', `Not offered for download: ${check.reason}`);
          const signed = await storage.signDownload(r.data.path, token, DOWNLOAD_SECONDS);
          return json(200, { url: signed.url, expiresAt: signed.expiresAt, scan: r.data.scan, inspection: check.zip ?? null,
            warning: 'Untrusted upload. Not malware-scanned unless "scan" says so. Open only in an isolated environment; never run its scripts.' });
        }
      }
    }
  } catch {
    return error(503, 'unavailable', 'HCG Executable Maker services are temporarily unavailable.');
  }
  return error(404, 'not_found');
}
