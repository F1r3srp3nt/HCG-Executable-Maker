// ERP ▸ Executable Maker support desk (/erp/executable-maker-support).
//
// For HCG staff with the ERP App Access product `executable-maker-support-desk`. Uses the website session (no OAuth
// client), through the same API as the app; the database decides everything (staff permission, audit trail).
// Attachments are untrusted: links are short-lived and offered only after the server re-inspected the file.
// Wrap the page in the ERP's own layout component if it has one (see README).
import { createFileRoute } from '@tanstack/react-router';
import { useCallback, useEffect, useState } from 'react';
import { supabase } from '@/integrations/supabase/client';

const API = '/api/public/executable-maker/v1/staff';
const HEADERS = { 'X-ExecutableMaker-Contract': 'hcg.executable-maker/1', 'Content-Type': 'application/json' };
const STATUSES = ['submitted', 'under_review', 'more_information_needed', 'in_progress', 'completed', 'closed'] as const;
const label = (s: string) => s.replace(/_/g, ' ').replace(/^./, (c) => c.toUpperCase());

async function api(path: string, init: RequestInit = {}) {
  const { data } = await supabase.auth.getSession();
  const token = data.session?.access_token;
  if (!token) throw new Error('Sign in to HCG first.');
  const res = await fetch(API + path, { ...init, headers: { ...HEADERS, Authorization: `Bearer ${token}` } });
  const body = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(body.message ?? body.error ?? `HTTP ${res.status}`);
  return body;
}

function SupportDesk() {
  const [filter, setFilter] = useState<string>('');
  const [list, setList] = useState<any[]>([]);
  const [selected, setSelected] = useState<any>(null);
  const [message, setMessage] = useState('');
  const [taskRef, setTaskRef] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      setError(null);
      setList((await api(`/requests${filter ? `?status=${filter}` : ''}`)).requests);
    } catch (e: any) {
      setError(e.message);
    }
  }, [filter]);
  useEffect(() => { void load(); }, [load]);

  const open = async (id: string) => {
    try {
      const r = (await api(`/requests/${id}`)).request;
      setSelected(r);
      setTaskRef(r.devTaskRef ?? '');
    } catch (e: any) { setError(e.message); }
  };
  const act = async (path: string, body: unknown) => {
    setBusy(true);
    try {
      setSelected((await api(`/requests/${selected.summary.id}/${path}`, { method: 'POST', body: JSON.stringify(body) })).request);
      setMessage('');
      void load();
    } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };
  const download = async (attachmentId: string) => {
    try {
      const r = await api(`/requests/${selected.summary.id}/attachments/${attachmentId}/download`, { method: 'POST' });
      if (window.confirm(`${r.warning}\n\nDownload now? The link expires at ${new Date(r.expiresAt).toLocaleTimeString()}.`)) window.location.href = r.url;
    } catch (e: any) { setError(e.message); }
  };

  return (
    <div className="mx-auto max-w-6xl p-6 space-y-4">
      <header>
        <p className="text-xs tracking-[0.3em] text-cyan-400">HCG ERP</p>
        <h1 className="text-2xl font-semibold">Executable Maker — project support</h1>
        <p className="text-sm text-muted-foreground">Requests from HCG Executable Maker users. A request never changes what the builder supports; link it to a build-adapter task instead.</p>
      </header>
      {error && <div className="rounded border border-red-500/40 bg-red-500/10 p-3 text-sm">{error}</div>}
      <div className="flex gap-2">
        <select className="rounded border bg-transparent p-2 text-sm" value={filter} onChange={(e) => setFilter(e.target.value)}>
          <option value="">All (except drafts)</option>
          {STATUSES.map((s) => <option key={s} value={s}>{label(s)}</option>)}
        </select>
        <button className="rounded border px-3 text-sm" onClick={() => void load()}>Refresh</button>
        <button className="rounded border px-3 text-sm" onClick={async () => { try { const r = await api('/purge', { method: 'POST' }); window.alert(`${r.deleted} expired attachment(s) deleted.`); } catch (e: any) { setError(e.message); } }}>
          Apply retention (delete expired attachments)
        </button>
      </div>
      <div className="grid grid-cols-5 gap-4">
        <ul className="col-span-2 space-y-1">
          {list.length === 0 && <li className="text-sm text-muted-foreground">No requests.</li>}
          {list.map((r) => (
            <li key={r.id}>
              <button onClick={() => void open(r.id)} className={`w-full rounded border p-2 text-left text-sm ${selected?.summary.id === r.id ? 'border-cyan-400' : ''}`}>
                <div className="font-medium">{r.reference} · {r.title}</div>
                <div className="text-xs text-muted-foreground">{label(r.status)} · {r.ownerEmail} · {new Date(r.updatedAt).toLocaleString()}</div>
              </button>
            </li>
          ))}
        </ul>
        <section className="col-span-3 space-y-3">
          {!selected ? <p className="text-sm text-muted-foreground">Select a request.</p> : (
            <>
              <h2 className="text-lg font-semibold">{selected.summary.reference} · {selected.summary.title}</h2>
              <p className="text-sm">{selected.ownerEmail} · {label(selected.summary.status)} · {selected.mode === 'diagnostics_only' ? 'Diagnostics only' : 'Diagnostics + files'}</p>
              <dl className="grid grid-cols-3 gap-1 text-sm">
                <dt className="text-muted-foreground">Goal</dt><dd className="col-span-2">{selected.details.goal}</dd>
                <dt className="text-muted-foreground">Language / framework</dt><dd className="col-span-2">{selected.details.languageFramework ?? '—'}</dd>
                <dt className="text-muted-foreground">Target</dt><dd className="col-span-2">{selected.details.targetOs ?? '—'} · {selected.details.outputFormat ?? '—'}</dd>
                <dt className="text-muted-foreground">Problem</dt><dd className="col-span-2 whitespace-pre-wrap">{selected.details.problem}</dd>
              </dl>
              <details><summary className="cursor-pointer text-sm">Diagnostics ({selected.diagnostics.length})</summary>
                <pre className="max-h-64 overflow-auto text-xs">{selected.diagnostics.map((d: any) => `[${d.section}] ${d.label}: ${d.value}`).join('\n')}</pre>
              </details>
              <div className="space-y-1">
                <h3 className="text-sm font-medium">Attachments (untrusted)</h3>
                {selected.attachments.length === 0 && <p className="text-xs text-muted-foreground">None.</p>}
                {selected.attachments.map((a: any) => (
                  <div key={a.id} className="flex items-center justify-between rounded border p-2 text-sm">
                    <span>{a.name} · {(a.sizeBytes / 1024).toFixed(0)} KB · {a.state} · scan: {a.scan}{a.deleteAfter ? ` · deleted after ${new Date(a.deleteAfter).toLocaleDateString()}` : ''}</span>
                    {a.state === 'received' && <button className="rounded border px-2" onClick={() => void download(a.id)}>Inspect &amp; download</button>}
                  </div>
                ))}
              </div>
              <div className="space-y-2">
                <h3 className="text-sm font-medium">Conversation</h3>
                {selected.messages.map((m: any) => (
                  <div key={m.id} className={`rounded p-2 text-sm ${m.fromStaff ? 'bg-cyan-500/10' : 'bg-muted'}`}>
                    <div className="text-xs text-muted-foreground">{m.fromStaff ? m.authorName ?? 'HCG' : 'Requester'} · {m.kind} · {new Date(m.createdAt).toLocaleString()}</div>
                    <div className="whitespace-pre-wrap">{m.body}</div>
                  </div>
                ))}
                <textarea className="w-full rounded border bg-transparent p-2 text-sm" rows={3} maxLength={4000} value={message} onChange={(e) => setMessage(e.target.value)} placeholder="Reply to the requester…" />
                <div className="flex flex-wrap gap-2">
                  <button disabled={busy || !message.trim()} className="rounded bg-cyan-500 px-3 py-1 text-sm text-black" onClick={() => void act('respond', { body: message, kind: 'response' })}>Send response</button>
                  <button disabled={busy || !message.trim()} className="rounded border px-3 py-1 text-sm" onClick={() => void act('respond', { body: message, kind: 'more_information' })}>Request more information</button>
                  <select disabled={busy} className="rounded border bg-transparent p-1 text-sm" value="" onChange={(e) => e.target.value && void act('status', { status: e.target.value, resolution: e.target.value === 'closed' ? 'not_planned' : null })}>
                    <option value="">Change status…</option>
                    {STATUSES.map((s) => <option key={s} value={s}>{label(s)}</option>)}
                  </select>
                </div>
                <div className="flex gap-2">
                  <input className="flex-1 rounded border bg-transparent p-1 text-sm" value={taskRef} onChange={(e) => setTaskRef(e.target.value)} placeholder="Build-adapter development task (ERP task id or URL)" />
                  <button disabled={busy} className="rounded border px-3 text-sm" onClick={() => void act('task', { taskRef })}>Link task</button>
                </div>
              </div>
              <details><summary className="cursor-pointer text-sm">Audit trail</summary>
                <ul className="text-xs">{selected.audit.map((x: any, i: number) => <li key={i}>{new Date(x.at).toLocaleString()} · {x.action} · {x.actor ?? 'system'}</li>)}</ul>
              </details>
            </>
          )}
        </section>
      </div>
    </div>
  );
}

export const Route = createFileRoute('/erp_/executable-maker-support')({ component: SupportDesk });
