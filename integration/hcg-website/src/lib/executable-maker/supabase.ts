// Database functions and Storage, both called with the CALLER's own access token and the project's public
// (publishable/anon) key. PostgREST and Storage verify the token; RLS and the SECURITY DEFINER functions authorize from
// its claims. No service-role key is used or needed.
import { createClient } from '@supabase/supabase-js';
import type { Rpc, SupportStorage } from './api.ts';

const BUCKET = 'executable-maker-support';

function clientFor(url: string, key: string, accessToken: string) {
  return createClient(url, key, {
    global: { headers: { Authorization: `Bearer ${accessToken}` } },
    auth: { persistSession: false, autoRefreshToken: false, detectSessionInUrl: false },
  });
}

export function supabaseRpc(url: string, key: string): Rpc {
  return async (fn, args, accessToken) => {
    const { data, error, status } = await clientFor(url, key, accessToken).rpc(fn, args);
    return { data, error: error ? { code: error.code, message: error.message, details: error.details } : null, status };
  };
}

export function supabaseStorage(url: string, key: string): SupportStorage {
  return {
    async signUpload(path, accessToken) {
      // Requires the bucket's insert policy to allow this caller and path (exm.may_upload).
      const { data, error } = await clientFor(url, key, accessToken).storage.from(BUCKET).createSignedUploadUrl(path);
      if (error || !data) throw new Error(error?.message ?? 'upload URL refused');
      // Supabase signed upload URLs are valid for 2 hours.
      return { url: data.signedUrl, expiresAt: new Date(Date.now() + 2 * 3600_000).toISOString() };
    },
    async signDownload(path, accessToken, seconds) {
      const { data, error } = await clientFor(url, key, accessToken).storage.from(BUCKET).createSignedUrl(path, seconds, { download: true });
      if (error || !data) throw new Error(error?.message ?? 'download URL refused');
      return { url: data.signedUrl, expiresAt: new Date(Date.now() + seconds * 1000).toISOString() };
    },
    async download(path, accessToken) {
      const { data, error } = await clientFor(url, key, accessToken).storage.from(BUCKET).download(path);
      if (error || !data) throw new Error(error?.message ?? 'download refused');
      return new Uint8Array(await data.arrayBuffer());
    },
    async remove(paths, accessToken) {
      const { error } = await clientFor(url, key, accessToken).storage.from(BUCKET).remove(paths);
      if (error) throw new Error(error.message);
    },
  };
}

/** The website's Supabase URL and public key from its environment (Lovable Cloud names first). */
export function fromEnv(): { rpc: Rpc; storage: SupportStorage } {
  const env = (typeof process !== 'undefined' ? process.env : {}) as Record<string, string | undefined>;
  const url = env.SUPABASE_URL ?? env.VITE_SUPABASE_URL;
  const key = env.SUPABASE_PUBLISHABLE_KEY ?? env.VITE_SUPABASE_PUBLISHABLE_KEY ?? env.SUPABASE_ANON_KEY;
  if (!url || !key) throw new Error('SUPABASE_URL and SUPABASE_PUBLISHABLE_KEY must be set for the Executable Maker API.');
  return { rpc: supabaseRpc(url, key), storage: supabaseStorage(url, key) };
}
