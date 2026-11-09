// MOCK HCG for verification only. It is never HCG and never contacted by a normal build.
//
// Stands in for HCG's identity server (OAuth 2.1 authorization code + PKCE S256, public client, ES256 ID tokens,
// refresh-token rotation, userinfo, JWKS, discovery) and HCG's central Ecosystem API (/api/public/ecosystem/v1/me,
// features, check), following docs/hcg-connect/HANDOFF.md. The authorize step approves at once, as if the user had
// signed in and consented, so a packaged app can be verified without a person.
//
// Usage: node tools/hcg-mock/mock-hcg.mjs <info.json> [client-id]
//   writes {"port":…,"issuer":…,"clientId":…} to info.json once listening on 127.0.0.1.
// Control (loopback only): POST /__mock/scenario {plan, licence, organisations, links, features} · GET /__mock/state
import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs';

const infoPath = process.argv[2];
const CLIENT_ID = process.argv[3] ?? '7d3c3f2a-5b1e-4c6d-9a8f-0e1d2c3b4a59';
const SUB = '9b2d7c1e-0000-4000-8000-00000000c0de';
const REDIRECTS = [47820, 47821, 47822].map(p => `http://127.0.0.1:${p}/oauth/callback`);
const { privateKey, publicKey } = crypto.generateKeyPairSync('ec', { namedCurve: 'P-256' });
const jwk = { ...publicKey.export({ format: 'jwk' }), kid: 'mock-1', alg: 'ES256', use: 'sig' };

const codes = new Map();          // code -> { challenge, redirect, nonce, scope }
const accessTokens = new Map();   // token -> { exp }
const refreshTokens = new Map();  // token -> 'live' | 'used'
const state = { authorizations: 0, codeExchanges: 0, refreshes: 0, rejected: [] };
let scenario = {
  organisations: [{ org_id: 'org-hcg', name: 'Hakmi Capital Group', role: 'owner' }],
  plan: { plan_key: 'pro', name: 'Pro', billing_type: 'consumer_subscription' },
  licence: { status: 'active', license_type: 'subscription', starts_at: '2026-11-01T00:00:00Z', ends_at: '2027-11-01T00:00:00Z' },
  features: [
    { feature_key: 'support.submit', display_name: 'Support requests', allowed: true },
    { feature_key: 'support.attach', display_name: 'Attach project files', allowed: true },
    { feature_key: 'ai.use', display_name: 'AI assistance', allowed: false },
  ],
  links: { manage: 'https://beta.hakmicapitalgroup.com/client-portal', account: 'https://beta.hakmicapitalgroup.com/client-portal' },
};

const b64u = b => Buffer.from(b).toString('base64url');
function jwt(claims) {
  const head = b64u(JSON.stringify({ alg: 'ES256', kid: jwk.kid, typ: 'JWT' }));
  const body = b64u(JSON.stringify(claims));
  const sig = crypto.sign('sha256', Buffer.from(`${head}.${body}`), { key: privateKey, dsaEncoding: 'ieee-p1363' });
  return `${head}.${body}.${b64u(sig)}`;
}
const now = () => Math.floor(Date.now() / 1000);
const json = (res, status, body, headers = {}) => { res.writeHead(status, { 'content-type': 'application/json', 'cache-control': 'no-store', ...headers }); res.end(JSON.stringify(body)); };
const oauthError = (res, status, error, error_code, description) => { state.rejected.push(error_code ?? error); json(res, status, { error, error_code, error_description: description }); };
const apiError = (res, status, code, message) => json(res, status, { error: { code, message }, api_version: 'v1' });
const readBody = req => new Promise(r => { let d = ''; req.on('data', c => d += c); req.on('end', () => r(d)); });

function issueTokens(scope, nonce) {
  const access = jwt({ iss: issuer, sub: SUB, aud: 'authenticated', role: 'authenticated', client_id: CLIENT_ID, scope, exp: now() + 3600, iat: now(), session_id: crypto.randomUUID() });
  accessTokens.set(access, { exp: now() + 3600 });
  const refresh = crypto.randomBytes(24).toString('base64url');
  refreshTokens.set(refresh, 'live');
  const body = { access_token: access, token_type: 'bearer', expires_in: 3600, refresh_token: refresh };
  if (nonce !== undefined) body.id_token = jwt({ iss: issuer, aud: CLIENT_ID, sub: SUB, iat: now(), exp: now() + 3600, nonce, email: 'founder@example.test', name: 'Test Founder' });
  return body;
}
function bearer(req) {
  const t = (req.headers.authorization ?? '').replace(/^Bearer /, '');
  const a = accessTokens.get(t);
  return a && a.exp > now() ? t : null;
}

let issuer = '';
const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, issuer || 'http://127.0.0.1');
  const p = url.pathname;
  try {
    if (p === '/auth/v1/.well-known/openid-configuration')
      return json(res, 200, { issuer, authorization_endpoint: `${issuer}/oauth/authorize`, token_endpoint: `${issuer}/oauth/token`, userinfo_endpoint: `${issuer}/oauth/userinfo`,
        jwks_uri: `${issuer}/.well-known/jwks.json`, scopes_supported: ['openid', 'profile', 'email', 'phone', 'offline_access'], code_challenge_methods_supported: ['S256'],
        grant_types_supported: ['authorization_code', 'refresh_token'], token_endpoint_auth_methods_supported: ['none'] });
    if (p === '/auth/v1/.well-known/jwks.json') return json(res, 200, { keys: [jwk] });
    if (p === '/auth/v1/oauth/authorize' && req.method === 'GET') {
      const q = url.searchParams;
      if (q.get('client_id') !== CLIENT_ID) return oauthError(res, 400, 'invalid_client', 'invalid_client', 'unknown client');
      if (!REDIRECTS.includes(q.get('redirect_uri'))) return oauthError(res, 400, 'invalid_request', 'invalid_redirect_uri', 'redirect_uri not registered (exact match)');
      if (q.get('response_type') !== 'code' || q.get('code_challenge_method') !== 'S256' || !q.get('code_challenge')) return oauthError(res, 400, 'invalid_request', 'pkce_required', 'PKCE S256 required');
      if (!(q.get('scope') ?? '').split(' ').includes('openid')) return oauthError(res, 400, 'invalid_scope', 'invalid_scope', 'openid required');
      state.authorizations++;
      const code = crypto.randomBytes(16).toString('base64url');
      codes.set(code, { challenge: q.get('code_challenge'), redirect: q.get('redirect_uri'), nonce: q.get('nonce') ?? '', scope: q.get('scope') });
      const back = new URL(q.get('redirect_uri'));
      back.searchParams.set('code', code);
      back.searchParams.set('state', q.get('state') ?? '');
      res.writeHead(302, { location: back.toString() });
      return res.end();
    }
    if (p === '/auth/v1/oauth/token' && req.method === 'POST') {
      const f = new URLSearchParams(await readBody(req));
      if (f.has('client_secret') || req.headers.authorization) return oauthError(res, 400, 'invalid_client', 'public_client', 'public clients send no secret');
      if (f.get('client_id') !== CLIENT_ID) return oauthError(res, 400, 'invalid_client', 'invalid_client', 'unknown client');
      if (f.get('grant_type') === 'authorization_code') {
        const c = codes.get(f.get('code'));
        codes.delete(f.get('code'));
        if (!c || c.redirect !== f.get('redirect_uri')) return oauthError(res, 400, 'invalid_grant', 'invalid_grant', 'bad code');
        const challenge = crypto.createHash('sha256').update(f.get('code_verifier') ?? '').digest('base64url');
        if (challenge !== c.challenge) return oauthError(res, 400, 'invalid_grant', 'invalid_grant', 'PKCE verification failed');
        state.codeExchanges++;
        return json(res, 200, issueTokens(c.scope, c.nonce));
      }
      if (f.get('grant_type') === 'refresh_token') {
        const r = f.get('refresh_token');
        const s = refreshTokens.get(r);
        if (s === undefined) return oauthError(res, 400, 'invalid_grant', 'refresh_token_not_found', 'Invalid Refresh Token: Refresh Token Not Found');
        if (s === 'used') return oauthError(res, 400, 'invalid_grant', 'refresh_token_already_used', 'Invalid Refresh Token: Already Used');
        refreshTokens.set(r, 'used'); // rotation: each refresh token works once
        state.refreshes++;
        return json(res, 200, issueTokens('openid email profile')); // no id_token on refresh
      }
      return oauthError(res, 400, 'unsupported_grant_type', 'unsupported_grant_type', 'grant');
    }
    if (p === '/auth/v1/oauth/userinfo') {
      if (!bearer(req)) return json(res, 401, { error: 'invalid_token' });
      return json(res, 200, { sub: SUB, email: 'founder@example.test', email_verified: true, name: 'Test Founder' });
    }
    if (p.startsWith('/api/public/ecosystem/v1/')) {
      if (!bearer(req)) return apiError(res, 401, 'unauthorized', 'Missing, invalid or expired access token.');
      const route = p.slice('/api/public/ecosystem/v1/'.length);
      if (route === 'me' && req.method === 'GET')
        return json(res, 200, { account: { id: SUB, email: 'founder@example.test', display_name: 'Test Founder' }, organisations: scenario.organisations, api_version: 'v1' });
      if (route === 'features' && req.method === 'GET') {
        const org = url.searchParams.get('org');
        if (scenario.organisations.length > 0 && !scenario.organisations.some(o => o.org_id === org))
          return apiError(res, 403, 'forbidden', 'Not a member of that organisation.');
        return json(res, 200, { product_id: url.searchParams.get('product'), org_id: org, features: scenario.features, plan: scenario.plan, licence: scenario.licence, links: scenario.links, api_version: 'v1' });
      }
      if (route === 'check' && req.method === 'POST') {
        const b = JSON.parse(await readBody(req) || '{}');
        const f = scenario.features.find(x => x.feature_key === b.feature_key);
        return json(res, 200, f?.allowed ? { allowed: true } : { allowed: false, blocking_layer: f ? 'feature' : 'app_access', message: 'Not granted by HCG.' });
      }
      return apiError(res, 404, 'not_found', 'No such route.');
    }
    if (p === '/__mock/scenario' && req.method === 'POST') {
      scenario = { ...scenario, ...JSON.parse(await readBody(req)) };
      return json(res, 200, scenario);
    }
    if (p === '/__mock/state') return json(res, 200, { ...state, liveRefreshTokens: [...refreshTokens.values()].filter(v => v === 'live').length });
    // Like the live site: routes that do not exist answer with the website's HTML 404 page.
    res.writeHead(404, { 'content-type': 'text/html; charset=utf-8' });
    res.end('<!DOCTYPE html><html><body>Not found</body></html>');
  } catch (e) {
    json(res, 500, { error: 'mock_failure', message: String(e) });
  }
});

server.listen(Number(process.env.MOCK_HCG_PORT ?? 0), '127.0.0.1', () => {
  const { port } = server.address();
  issuer = `http://127.0.0.1:${port}/auth/v1`;
  const info = { port, issuer, apiBaseUrl: `http://127.0.0.1:${port}`, clientId: CLIENT_ID };
  if (infoPath) fs.writeFileSync(infoPath, JSON.stringify(info));
  console.log(`MOCK HCG (not HCG) on ${info.apiBaseUrl}, client ${CLIENT_ID}`);
});
