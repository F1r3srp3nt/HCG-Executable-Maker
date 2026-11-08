-- HCG Executable Maker × HCG website: App Access, project-support requests and attachments.
--
-- Drop-in migration for the HCG website's existing Supabase project (qggdyzoydveafkmjgkpq), following the pattern of
-- the LifeOS package (hcg-lifeos/integration/hcg-website). NOT DEPLOYED: applying it is the founder's action.
--
-- Principles
--   * No service-role key anywhere. Every public function authorizes the caller from the verified access token alone
--     (auth.jwt(): sub, session_id, client_id), so direct PostgREST calls get exactly the same checks as the website's
--     API routes. Tables have RLS on, no policies and no grants: functions only.
--   * The ERP is the authority. Permissions come from ERP App Access (app_effective_entitlements / app_entitlements /
--     app_applications.product_id) through one adapter, exm.entitlement(). No role system is created here.
--   * The token's session must still exist (sign-out / revocation on HCG ends access at once). Banned accounts are refused.
--   * Channel from client_id: a registered Executable Maker client => 'app'; no client_id => 'website' (the ERP staff
--     desk); any other OAuth client (LifeOS, CardX, …) => 403.
--   * Attachments live in the private bucket 'executable-maker-support' under <owner>/<request>/<attachment>. Storage
--     RLS allows an upload only to a pending attachment of the caller's own draft, reads only to the owner and to
--     support staff. Uploaded files are untrusted: never extracted or executed here.
--   * A support request never changes what the builder supports.

create schema if not exists exm;
revoke all on schema exm from public;

-- ---------------------------------------------------------------------------------------------------------------------
-- Configuration
-- ---------------------------------------------------------------------------------------------------------------------

-- Executable Maker's registered OAuth client(s). EMPTY until the client is registered (see docs/HCG-ACCOUNT.md):
--   insert into exm.clients (client_id, label) values ('<client id>', 'HCG Executable Maker');
create table if not exists exm.clients (
    client_id uuid primary key,
    label text not null,
    created_at timestamptz not null default now()
);

create table if not exists exm.settings (
    key text primary key,
    value jsonb not null
);
insert into exm.settings (key, value) values
    -- ERP App Access products (PROPOSED ids; register them in ERP > App Access, rename here if needed).
    ('app_product', '"executable-maker"'),               -- account features: support requests (+ AI when the entitlement's features grant it)
    ('staff_product', '"executable-maker-support-desk"'), -- HCG staff who handle Executable Maker support requests
    -- Limits (also enforced by the bucket's file_size_limit).
    ('max_attachment_bytes', '52428800'),       -- 50 MB per file
    ('max_request_bytes', '104857600'),         -- 100 MB per request
    ('max_attachments', '3'),
    ('max_archive_expanded_bytes', '1073741824'), -- 1 GB uncompressed (checked by the website route before staff access)
    ('max_archive_entries', '20000'),
    ('max_requests_per_day', '10'),
    -- PROPOSED retention (no HCG-wide retention policy exists to reuse; requires founder approval):
    ('attachment_retention_days', '90')         -- attachments deleted 90 days after the request is completed/closed, at once on withdrawal
on conflict (key) do nothing;

create or replace function exm.setting(p_key text) returns jsonb
language sql stable security definer set search_path = '' as $$
    select s.value from exm.settings s where s.key = p_key
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Data
-- ---------------------------------------------------------------------------------------------------------------------

create table if not exists exm.requests (
    id uuid primary key default gen_random_uuid(),
    reference text not null unique,
    owner_subject uuid not null references auth.users (id) on delete cascade,
    organisation_id text,
    client_request_id uuid not null,
    title text not null check (char_length(title) between 3 and 200),
    details jsonb not null,
    diagnostics jsonb not null default '[]',
    mode text not null check (mode in ('diagnostics_only', 'diagnostics_and_files')),
    -- HCG ONE's request status convention (without its commercial states).
    status text not null default 'draft' check (status in ('draft', 'submitted', 'under_review', 'more_information_needed', 'in_progress', 'completed', 'closed')),
    resolution text check (resolution in ('withdrawn', 'resolved', 'not_planned', 'duplicate')),
    dev_task_ref text check (dev_task_ref is null or char_length(dev_task_ref) <= 300),
    app_version text,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    submitted_at timestamptz,
    closed_at timestamptz,
    unique (owner_subject, client_request_id)
);

create table if not exists exm.attachments (
    id uuid primary key default gen_random_uuid(),
    request_id uuid not null references exm.requests (id) on delete cascade,
    owner_subject uuid not null,
    name text not null check (name ~ '^[A-Za-z0-9._ ()+-]{1,120}$' and name not like '..%'),
    declared_bytes bigint not null check (declared_bytes > 0),
    declared_sha256 text not null check (declared_sha256 ~ '^[0-9a-f]{64}$'),
    content_type text not null check (content_type in ('application/zip', 'text/plain', 'application/json', 'application/octet-stream')),
    object_path text not null unique,
    state text not null default 'pending' check (state in ('pending', 'received', 'rejected', 'deleted')),
    reason text,
    scan text not null default 'not_scanned' check (scan in ('not_scanned', 'clean', 'flagged')),
    received_at timestamptz,
    delete_after timestamptz,
    deleted_at timestamptz
);

create table if not exists exm.messages (
    id uuid primary key default gen_random_uuid(),
    request_id uuid not null references exm.requests (id) on delete cascade,
    author_subject uuid,
    from_staff boolean not null,
    author_name text,
    body text not null check (char_length(body) between 1 and 4000),
    kind text not null check (kind in ('reply', 'response', 'more_information', 'status')),
    created_at timestamptz not null default now()
);

-- Audit trail: every status change, staff view, staff attachment access, response, task link, deletion.
create table if not exists exm.audit (
    id bigserial primary key,
    at timestamptz not null default now(),
    actor_subject uuid,
    actor_channel text,
    action text not null,
    request_id uuid,
    attachment_id uuid,
    detail jsonb not null default '{}'
);

create index if not exists exm_requests_owner on exm.requests (owner_subject, created_at desc);
create index if not exists exm_requests_status on exm.requests (status, updated_at desc);
create index if not exists exm_attachments_request on exm.attachments (request_id);
create index if not exists exm_messages_request on exm.messages (request_id, created_at);
create index if not exists exm_audit_request on exm.audit (request_id, at);

alter table exm.clients enable row level security;
alter table exm.settings enable row level security;
alter table exm.requests enable row level security;
alter table exm.attachments enable row level security;
alter table exm.messages enable row level security;
alter table exm.audit enable row level security;
revoke all on all tables in schema exm from public, anon, authenticated;

-- ---------------------------------------------------------------------------------------------------------------------
-- Caller, ERP adapter, permissions
-- ---------------------------------------------------------------------------------------------------------------------

-- Raises an error PostgREST/the website route turn into HTTP p_status with {"error": p_code, "message": p_message}.
create or replace function exm.fail(p_status integer, p_code text, p_message text) returns void
language plpgsql as $$
begin
    raise exception using errcode = 'PT' || p_status::text, message = p_code, detail = p_message;
end $$;

create or replace function exm.is_uuid(p_text text) returns boolean
language sql immutable as $$ select coalesce(p_text ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$', false) $$; -- NULL is not a UUID

create or replace function exm.caller(out subject uuid, out client_id uuid, out channel text)
language plpgsql stable security definer set search_path = '' as $$
declare
    v_claims jsonb := auth.jwt();
    v_sub text := v_claims ->> 'sub';
    v_session text := v_claims ->> 'session_id';
    v_client text := v_claims ->> 'client_id';
begin
    if v_sub is null or not exm.is_uuid(v_sub) or coalesce(v_claims ->> 'role', '') <> 'authenticated' then
        perform exm.fail(401, 'unauthorized', 'Missing, invalid or expired session.');
    end if;
    if v_session is null or not exm.is_uuid(v_session) or not exists (
        select 1 from auth.sessions s
        where s.id = v_session::uuid and s.user_id = v_sub::uuid and (s.not_after is null or s.not_after > now())
    ) then
        perform exm.fail(401, 'unauthorized', 'Missing, invalid or expired session.');
    end if;
    if exists (select 1 from auth.users u where u.id = v_sub::uuid and u.banned_until is not null and u.banned_until > now()) then
        perform exm.fail(401, 'unauthorized', 'Missing, invalid or expired session.');
    end if;
    subject := v_sub::uuid;
    if v_client is null or v_client = '' then
        channel := 'website';
    elsif exm.is_uuid(v_client) and exists (select 1 from exm.clients c where c.client_id = v_client::uuid) then
        client_id := v_client::uuid;
        channel := 'app';
    else
        perform exm.fail(403, 'forbidden', 'This application is not HCG Executable Maker.');
    end if;
end $$;

-- ERP App Access for one account and product: state 'granted' | 'revoked' | 'none', plus the entitlement's features.
-- Single adapter point: with the ERP installed its entitlements are the only source; nothing here grants access.
create or replace function exm.entitlement(p_user uuid, p_product text, out state text, out features jsonb)
language plpgsql stable security definer set search_path = '' as $$
declare
    v_row jsonb;
    v_revoked boolean := false;
begin
    state := 'none';
    features := '{}'::jsonb;
    if to_regprocedure('public.app_effective_entitlements(uuid)') is null then
        return; -- ERP App Access not installed: nobody has access (no fallback grants in this package)
    end if;
    for v_row in execute 'select to_jsonb(e) from public.app_effective_entitlements($1) e' using p_user loop
        if v_row ->> 'product_id' = p_product
           and coalesce(v_row ->> 'active', 'true') <> 'false'
           and coalesce(v_row ->> 'status', 'active') in ('active', 'granted')
           and (v_row ->> 'revoked_at') is null
           and ((v_row ->> 'expires_at') is null or (v_row ->> 'expires_at')::timestamptz > now()) then
            state := 'granted';
            features := coalesce(v_row -> 'features', '{}'::jsonb);
            return;
        end if;
    end loop;
    if to_regclass('public.app_entitlements') is not null and to_regclass('public.app_applications') is not null then
        execute 'select exists (select 1 from public.app_entitlements e join public.app_applications a on a.id = e.application_id
                 where e.employee_id = $1 and a.product_id = $2 and (e.revoked_at is not null or e.status in (''revoked'', ''suspended'')))'
           into v_revoked using p_user, p_product;
    end if;
    if v_revoked then state := 'revoked'; end if;
end $$;

-- Organisation membership from the ERP. No ERP organisation contract was found in any reachable source, so this
-- adapter returns none until HCG names the object to read (then: return [{id, name, role}] for p_user).
create or replace function exm.organisations(p_user uuid) returns jsonb
language sql stable security definer set search_path = '' as $$ select '[]'::jsonb $$;

-- Permissions this caller has for Executable Maker, derived only from ERP entitlements.
create or replace function exm.permissions(p_user uuid, p_channel text) returns text[]
language plpgsql stable security definer set search_path = '' as $$
declare
    v_app record;
    v_staff record;
    v_perms text[] := '{}';
begin
    select * into v_app from exm.entitlement(p_user, exm.setting('app_product') #>> '{}');
    select * into v_staff from exm.entitlement(p_user, exm.setting('staff_product') #>> '{}');
    if v_app.state = 'granted' then
        v_perms := v_perms || array['support.submit', 'support.attach'];
        if coalesce(v_app.features ->> 'ai', 'false') = 'true' then v_perms := array_append(v_perms, 'ai.use'); end if;
    end if;
    if v_staff.state = 'granted' and p_channel = 'website' then v_perms := array_append(v_perms, 'support.staff'); end if;
    return v_perms;
end $$;

-- The caller must hold p_permission; revoked app access answers 403 revoked (the app then removes its session).
create or replace function exm.require(p_permission text, out subject uuid, out client_id uuid, out channel text)
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    v_state record;
begin
    select * into v_caller from exm.caller();
    if p_permission = 'support.staff' then
        if v_caller.channel <> 'website' then perform exm.fail(403, 'forbidden', 'The support desk is part of HCG ERP (website session).'); end if;
    elsif v_caller.channel <> 'app' then
        perform exm.fail(403, 'forbidden', 'This API is for HCG Executable Maker.');
    end if;
    if not (p_permission = any (exm.permissions(v_caller.subject, v_caller.channel))) then
        select * into v_state from exm.entitlement(v_caller.subject, exm.setting(case when p_permission = 'support.staff' then 'staff_product' else 'app_product' end) #>> '{}');
        if v_state.state = 'revoked' then perform exm.fail(403, 'revoked', 'HCG has withdrawn HCG Executable Maker access for this account.'); end if;
        perform exm.fail(403, 'forbidden', 'Your HCG account does not have this permission (ERP > App Access).');
    end if;
    subject := v_caller.subject;
    client_id := v_caller.client_id;
    channel := v_caller.channel;
end $$;

-- An X-HCG-Organisation value must be one of the caller's organisations.
create or replace function exm.checked_organisation(p_user uuid, p_organisation text) returns text
language plpgsql stable security definer set search_path = '' as $$
begin
    if p_organisation is null or p_organisation = '' then return null; end if;
    if not exists (select 1 from jsonb_array_elements(exm.organisations(p_user)) o where o ->> 'id' = p_organisation) then
        perform exm.fail(403, 'forbidden', 'You are not a member of that organisation.');
    end if;
    return p_organisation;
end $$;

create or replace function exm.audit_log(p_actor uuid, p_channel text, p_action text, p_request uuid, p_attachment uuid, p_detail jsonb default '{}')
returns void language sql security definer set search_path = '' as $$
    insert into exm.audit (actor_subject, actor_channel, action, request_id, attachment_id, detail)
    values (p_actor, p_channel, p_action, p_request, p_attachment, coalesce(p_detail, '{}'))
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- App Access directory (GET /access)
-- ---------------------------------------------------------------------------------------------------------------------

create or replace function public.exm_access_directory(p_organisation text default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    v_user jsonb;
    v_roles jsonb := '[]'::jsonb;
    v_app record;
    v_perms text[];
    v_org text;
begin
    select * into v_caller from exm.caller();
    if v_caller.channel <> 'app' then perform exm.fail(403, 'forbidden', 'This API is for HCG Executable Maker.'); end if;
    v_org := exm.checked_organisation(v_caller.subject, p_organisation);
    select jsonb_build_object('id', u.id, 'email', u.email,
               'displayName', coalesce(u.raw_user_meta_data ->> 'display_name', u.raw_user_meta_data ->> 'full_name', u.raw_user_meta_data ->> 'name'))
      into v_user from auth.users u where u.id = v_caller.subject;
    if to_regclass('public.user_roles') is not null then
        execute 'select coalesce(jsonb_agg(r.role::text order by r.role::text), ''[]''::jsonb) from public.user_roles r where r.user_id = $1'
           into v_roles using v_caller.subject;
    end if;
    select * into v_app from exm.entitlement(v_caller.subject, exm.setting('app_product') #>> '{}');
    if v_app.state = 'revoked' then perform exm.fail(403, 'revoked', 'HCG has withdrawn HCG Executable Maker access for this account.'); end if;
    v_perms := exm.permissions(v_caller.subject, v_caller.channel);
    return jsonb_build_object(
        'user', v_user,
        'roles', v_roles,
        'workspaces', jsonb_build_array(jsonb_build_object(
            'id', exm.setting('app_product') #>> '{}', 'title', 'HCG Executable Maker', 'route', null,
            'permitted', v_app.state = 'granted', 'available', true,
            'requires', 'HCG Executable Maker access in ERP App Access')),
        'permissions', to_jsonb(v_perms),
        'organisations', exm.organisations(v_caller.subject),
        'organisation', v_org,
        'limits', jsonb_build_object(
            'maxAttachmentBytes', exm.setting('max_attachment_bytes'), 'maxRequestBytes', exm.setting('max_request_bytes'),
            'maxAttachments', exm.setting('max_attachments'), 'maxArchiveExpandedBytes', exm.setting('max_archive_expanded_bytes'),
            'maxArchiveEntries', exm.setting('max_archive_entries'), 'attachmentRetentionDays', exm.setting('attachment_retention_days')),
        'integration', jsonb_build_object('version', 1, 'contract', 'hcg.executable-maker/1'));
end $$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Owner side (the app)
-- ---------------------------------------------------------------------------------------------------------------------

create or replace function exm.reference() returns text
language plpgsql volatile security definer set search_path = '' as $$
declare
    v text;
begin
    loop
        v := 'HCG-' || upper(substr(translate(replace(gen_random_uuid()::text, '-', ''), '01', ''), 1, 6));
        exit when char_length(v) = 10 and not exists (select 1 from exm.requests r where r.reference = v);
    end loop;
    return v;
end $$;

create or replace function exm.summary(p_r exm.requests) returns jsonb
language sql stable security definer set search_path = '' as $$
    select jsonb_build_object('id', p_r.id, 'reference', p_r.reference, 'title', p_r.title,
        'status', p_r.status, 'createdAt', p_r.created_at, 'updatedAt', p_r.updated_at,
        'staffMessages', (select count(*) from exm.messages m where m.request_id = p_r.id and m.from_staff),
        'lastStaffMessageAt', (select max(m.created_at) from exm.messages m where m.request_id = p_r.id and m.from_staff))
$$;

create or replace function exm.attachment_json(p_a exm.attachments) returns jsonb
language sql stable as $$
    select jsonb_build_object('id', p_a.id, 'name', p_a.name, 'sizeBytes', p_a.declared_bytes, 'state', p_a.state,
        'reason', p_a.reason, 'scan', p_a.scan, 'deleteAfter', p_a.delete_after)
$$;

create or replace function exm.detail(p_id uuid) returns jsonb
language sql stable security definer set search_path = '' as $$
    select jsonb_build_object(
        'summary', exm.summary(r), 'details', r.details, 'mode', r.mode, 'diagnostics', r.diagnostics,
        'attachments', coalesce((select jsonb_agg(exm.attachment_json(a) order by a.name) from exm.attachments a where a.request_id = r.id), '[]'),
        'messages', coalesce((select jsonb_agg(jsonb_build_object('id', m.id, 'fromStaff', m.from_staff, 'authorName', m.author_name,
                     'body', m.body, 'kind', m.kind, 'createdAt', m.created_at) order by m.created_at) from exm.messages m where m.request_id = r.id), '[]'),
        'retentionNote', format('Attachments are deleted %s days after the request is completed or closed, and immediately if you withdraw it. Diagnostics and messages are kept with the request.',
                                exm.setting('attachment_retention_days') #>> '{}'))
    from exm.requests r where r.id = p_id
$$;

-- Loads the caller's own request (404 for anyone else's: existence is not revealed).
create or replace function exm.own_request(p_owner uuid, p_id uuid, p_lock boolean default false) returns exm.requests
language plpgsql security definer set search_path = '' as $$
declare
    v exm.requests;
begin
    if p_lock then select * into v from exm.requests r where r.id = p_id and r.owner_subject = p_owner for update;
    else select * into v from exm.requests r where r.id = p_id and r.owner_subject = p_owner; end if;
    if v.id is null then perform exm.fail(404, 'not_found', 'No such request.'); end if;
    return v;
end $$;

-- POST /support/requests. Idempotent per (owner, clientRequestId): a repeat returns the same request ("duplicate").
-- Returns the pending attachments' object paths; the website route turns them into signed upload URLs.
create or replace function public.exm_support_create(p_request jsonb, p_organisation text default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    v_existing exm.requests;
    v_req exm.requests;
    v_mode text;
    v_att jsonb;
    v_total bigint := 0;
    v_count int := 0;
    v_client uuid;
    v_org text;
    v_details jsonb := p_request -> 'details';
begin
    select * into v_caller from exm.require('support.submit');
    v_org := exm.checked_organisation(v_caller.subject, p_organisation);
    if not exm.is_uuid(p_request ->> 'clientRequestId') then perform exm.fail(400, 'invalid', 'clientRequestId must be a UUID.'); end if;
    v_client := (p_request ->> 'clientRequestId')::uuid;

    select * into v_existing from exm.requests r where r.owner_subject = v_caller.subject and r.client_request_id = v_client for update;
    if v_existing.id is not null then
        return jsonb_build_object('request', exm.summary(v_existing), 'duplicate', true,
            'uploads', coalesce((select jsonb_agg(jsonb_build_object('attachmentId', a.id, 'name', a.name, 'path', a.object_path,
                       'maxBytes', exm.setting('max_attachment_bytes'))) from exm.attachments a
                       where a.request_id = v_existing.id and a.state = 'pending' and v_existing.status = 'draft'), '[]'));
    end if;

    if (select count(*) from exm.requests r where r.owner_subject = v_caller.subject and r.created_at > now() - interval '1 day')
       >= (exm.setting('max_requests_per_day') #>> '{}')::int then
        perform exm.fail(429, 'rate_limited', 'Too many support requests today. Try again tomorrow.');
    end if;
    v_mode := case p_request ->> 'mode' when 'diagnostics_only' then 'diagnostics_only' when 'diagnostics_and_files' then 'diagnostics_and_files' end;
    if v_mode is null then perform exm.fail(400, 'invalid', 'mode must be diagnostics_only or diagnostics_and_files.'); end if;
    if jsonb_typeof(v_details) <> 'object' or char_length(coalesce(v_details ->> 'goal', '')) not between 3 and 2000
       or char_length(coalesce(v_details ->> 'problem', '')) not between 3 and 4000 then
        perform exm.fail(400, 'invalid', 'Describe what you are trying to build and what failed.');
    end if;
    if jsonb_typeof(coalesce(p_request -> 'diagnostics', '[]')) <> 'array' or octet_length((p_request -> 'diagnostics')::text) > 262144 then
        perform exm.fail(413, 'invalid', 'Diagnostics must be a list of at most 256 KB.');
    end if;
    if v_mode = 'diagnostics_only' and jsonb_array_length(coalesce(p_request -> 'attachments', '[]')) > 0 then
        perform exm.fail(400, 'invalid', 'A diagnostics-only request carries no attachments.');
    end if;
    if v_mode = 'diagnostics_and_files' then
        perform exm.require('support.attach');
        if jsonb_array_length(coalesce(p_request -> 'attachments', '[]')) = 0 then perform exm.fail(400, 'invalid', 'Select at least one file.'); end if;
    end if;

    insert into exm.requests (reference, owner_subject, organisation_id, client_request_id, title, details, diagnostics, mode, app_version)
    values (exm.reference(), v_caller.subject, v_org, v_client, left(coalesce(p_request ->> 'title', 'Support request'), 200),
            jsonb_build_object('goal', v_details ->> 'goal', 'languageFramework', v_details ->> 'languageFramework',
                               'targetOs', v_details ->> 'targetOs', 'outputFormat', v_details ->> 'outputFormat', 'problem', v_details ->> 'problem'),
            coalesce(p_request -> 'diagnostics', '[]'), v_mode, left(p_request ->> 'appVersion', 40))
    returning * into v_req;

    for v_att in select * from jsonb_array_elements(coalesce(p_request -> 'attachments', '[]')) loop
        v_count := v_count + 1;
        v_total := v_total + coalesce((v_att ->> 'sizeBytes')::bigint, 0);
        if v_count > (exm.setting('max_attachments') #>> '{}')::int then perform exm.fail(400, 'invalid', 'Too many attachments.'); end if;
        if (v_att ->> 'sizeBytes')::bigint > (exm.setting('max_attachment_bytes') #>> '{}')::bigint then
            perform exm.fail(413, 'too_large', format('%s is larger than HCG accepts.', v_att ->> 'name'));
        end if;
        insert into exm.attachments (request_id, owner_subject, name, declared_bytes, declared_sha256, content_type, object_path)
        values (v_req.id, v_caller.subject, v_att ->> 'name', (v_att ->> 'sizeBytes')::bigint, lower(v_att ->> 'sha256'), v_att ->> 'contentType',
                v_caller.subject::text || '/' || v_req.id::text || '/' || gen_random_uuid()::text);
    end loop;
    if v_total > (exm.setting('max_request_bytes') #>> '{}')::bigint then perform exm.fail(413, 'too_large', 'The attachments are larger than HCG accepts per request.'); end if;

    if v_mode = 'diagnostics_only' then
        update exm.requests set status = 'submitted', submitted_at = now(), updated_at = now() where id = v_req.id returning * into v_req;
    end if;
    perform exm.audit_log(v_caller.subject, v_caller.channel, 'request_created', v_req.id, null, jsonb_build_object('mode', v_mode, 'attachments', v_count));
    return jsonb_build_object('request', exm.summary(v_req), 'duplicate', false,
        'uploads', coalesce((select jsonb_agg(jsonb_build_object('attachmentId', a.id, 'name', a.name, 'path', a.object_path,
                   'maxBytes', exm.setting('max_attachment_bytes'))) from exm.attachments a where a.request_id = v_req.id), '[]'));
end $$;

create or replace function public.exm_support_list() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
begin
    select * into v_caller from exm.require('support.submit');
    return jsonb_build_object('requests', coalesce((select jsonb_agg(exm.summary(r) order by r.created_at desc)
        from exm.requests r where r.owner_subject = v_caller.subject), '[]'));
end $$;

create or replace function public.exm_support_get(p_id uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    v exm.requests;
begin
    select * into v_caller from exm.require('support.submit');
    v := exm.own_request(v_caller.subject, p_id);
    return jsonb_build_object('request', exm.detail(v.id));
end $$;

-- A pending attachment of the caller's own draft: the object path to sign an upload URL for.
create or replace function public.exm_support_upload_target(p_id uuid, p_attachment uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    v exm.requests;
    a exm.attachments;
begin
    select * into v_caller from exm.require('support.attach');
    v := exm.own_request(v_caller.subject, p_id);
    select * into a from exm.attachments x where x.id = p_attachment and x.request_id = v.id;
    if a.id is null then perform exm.fail(404, 'not_found', 'No such attachment.'); end if;
    if v.status <> 'draft' or a.state <> 'pending' then perform exm.fail(409, 'conflict', 'This attachment is no longer waiting for an upload.'); end if;
    return jsonb_build_object('attachmentId', a.id, 'name', a.name, 'path', a.object_path, 'maxBytes', exm.setting('max_attachment_bytes'));
end $$;

-- Checks the uploaded object in storage (exists, at the expected path, size equals the declared size and the limit).
-- The checksum and archive structure are checked by the website route before staff can open the file.
create or replace function public.exm_support_complete_attachment(p_id uuid, p_attachment uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    v exm.requests;
    a exm.attachments;
    v_size bigint;
begin
    select * into v_caller from exm.require('support.attach');
    v := exm.own_request(v_caller.subject, p_id, true);
    select * into a from exm.attachments x where x.id = p_attachment and x.request_id = v.id for update;
    if a.id is null then perform exm.fail(404, 'not_found', 'No such attachment.'); end if;
    if a.state = 'received' then return jsonb_build_object('attachment', exm.attachment_json(a)); end if;
    if v.status <> 'draft' or a.state <> 'pending' then perform exm.fail(409, 'conflict', 'This attachment is no longer waiting for an upload.'); end if;
    select (o.metadata ->> 'size')::bigint into v_size from storage.objects o
     where o.bucket_id = 'executable-maker-support' and o.name = a.object_path;
    if v_size is null then perform exm.fail(409, 'conflict', 'The file has not arrived yet. Upload it again.'); end if;
    if v_size <> a.declared_bytes or v_size > (exm.setting('max_attachment_bytes') #>> '{}')::bigint then
        update exm.attachments set state = 'rejected', reason = format('The stored file is %s bytes; %s bytes were declared.', v_size, a.declared_bytes)
         where id = a.id returning * into a;
    else
        update exm.attachments set state = 'received', received_at = now() where id = a.id returning * into a;
    end if;
    perform exm.audit_log(v_caller.subject, v_caller.channel, 'attachment_' || a.state, v.id, a.id, jsonb_build_object('bytes', v_size));
    return jsonb_build_object('attachment', exm.attachment_json(a));
end $$;

-- The website route's own checks after upload (checksum, archive structure) can reject the owner's attachment.
-- Callable by the owner only, and only to reject: it can never mark a file as accepted.
create or replace function public.exm_support_reject_attachment(p_id uuid, p_attachment uuid, p_reason text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    v exm.requests;
    a exm.attachments;
begin
    select * into v_caller from exm.require('support.attach');
    v := exm.own_request(v_caller.subject, p_id, true);
    update exm.attachments set state = 'rejected', reason = left(coalesce(p_reason, 'Rejected by HCG intake checks.'), 300)
     where id = p_attachment and request_id = v.id and state in ('pending', 'received') returning * into a;
    if a.id is null then perform exm.fail(404, 'not_found', 'No such attachment.'); end if;
    perform exm.audit_log(v_caller.subject, v_caller.channel, 'attachment_rejected', v.id, a.id, jsonb_build_object('reason', a.reason));
    return jsonb_build_object('attachment', exm.attachment_json(a));
end $$;

-- Submits a draft once every attachment was received.
create or replace function public.exm_support_submit(p_id uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    v exm.requests;
begin
    select * into v_caller from exm.require('support.submit');
    v := exm.own_request(v_caller.subject, p_id, true);
    if v.status = 'draft' then
        if exists (select 1 from exm.attachments a where a.request_id = v.id and a.state <> 'received') then
            perform exm.fail(409, 'conflict', 'Every attachment must be uploaded and accepted before the request is submitted.');
        end if;
        update exm.requests set status = 'submitted', submitted_at = now(), updated_at = now() where id = v.id;
        perform exm.audit_log(v_caller.subject, v_caller.channel, 'request_submitted', v.id, null);
    end if;
    return jsonb_build_object('request', exm.detail(v.id));
end $$;

create or replace function public.exm_support_reply(p_id uuid, p_body text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    v exm.requests;
begin
    select * into v_caller from exm.require('support.submit');
    v := exm.own_request(v_caller.subject, p_id, true);
    if v.status in ('draft', 'closed', 'completed') then perform exm.fail(409, 'conflict', 'This request does not accept messages.'); end if;
    if char_length(coalesce(trim(p_body), '')) not between 1 and 4000 then perform exm.fail(400, 'invalid', 'Messages are 1–4000 characters.'); end if;
    insert into exm.messages (request_id, author_subject, from_staff, body, kind) values (v.id, v_caller.subject, false, trim(p_body), 'reply');
    update exm.requests set updated_at = now(),
        status = case when status = 'more_information_needed' then 'submitted' else status end where id = v.id;
    perform exm.audit_log(v_caller.subject, v_caller.channel, 'owner_replied', v.id, null);
    return jsonb_build_object('request', exm.detail(v.id));
end $$;

-- Withdraws the request: closed, and every attachment marked deleted now. Returns the object paths for the website
-- route to remove from storage (the owner's delete policy allows exactly these).
create or replace function public.exm_support_withdraw(p_id uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    v exm.requests;
    v_paths jsonb;
begin
    select * into v_caller from exm.require('support.submit');
    v := exm.own_request(v_caller.subject, p_id, true);
    select coalesce(jsonb_agg(a.object_path), '[]') into v_paths from exm.attachments a where a.request_id = v.id and a.state <> 'deleted';
    update exm.attachments set state = 'deleted', deleted_at = now(), delete_after = now() where request_id = v.id and state <> 'deleted';
    update exm.requests set status = 'closed', resolution = 'withdrawn', closed_at = now(), updated_at = now() where id = v.id;
    perform exm.audit_log(v_caller.subject, v_caller.channel, 'request_withdrawn', v.id, null);
    return jsonb_build_object('request', exm.detail(v.id), 'deletePaths', v_paths);
end $$;

-- The owner's own received attachment: the object path for a short-lived signed download link.
create or replace function public.exm_support_download_target(p_id uuid, p_attachment uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    v exm.requests;
    a exm.attachments;
begin
    select * into v_caller from exm.require('support.submit');
    v := exm.own_request(v_caller.subject, p_id);
    select * into a from exm.attachments x where x.id = p_attachment and x.request_id = v.id and x.state = 'received';
    if a.id is null then perform exm.fail(404, 'not_found', 'No such attachment.'); end if;
    return jsonb_build_object('path', a.object_path, 'sha256', a.declared_sha256, 'contentType', a.content_type);
end $$;

-- ---------------------------------------------------------------------------------------------------------------------
-- Staff desk (ERP, website session, ERP App Access product 'executable-maker-support-desk')
-- ---------------------------------------------------------------------------------------------------------------------

create or replace function public.exm_staff_list(p_status text default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
begin
    select * into v_caller from exm.require('support.staff');
    return jsonb_build_object('requests', coalesce((
        select jsonb_agg(exm.summary(r) || jsonb_build_object('ownerEmail', u.email, 'organisationId', r.organisation_id,
                  'devTaskRef', r.dev_task_ref, 'mode', r.mode) order by r.updated_at desc)
        from exm.requests r left join auth.users u on u.id = r.owner_subject
        where r.status <> 'draft' and (p_status is null or r.status = p_status)), '[]'));
end $$;

create or replace function public.exm_staff_get(p_id uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    v exm.requests;
begin
    select * into v_caller from exm.require('support.staff');
    select * into v from exm.requests r where r.id = p_id and r.status <> 'draft';
    if v.id is null then perform exm.fail(404, 'not_found', 'No such request.'); end if;
    perform exm.audit_log(v_caller.subject, v_caller.channel, 'staff_viewed', v.id, null);
    return jsonb_build_object('request', exm.detail(v.id) || jsonb_build_object(
        'ownerEmail', (select u.email from auth.users u where u.id = v.owner_subject), 'organisationId', v.organisation_id,
        'devTaskRef', v.dev_task_ref, 'resolution', v.resolution,
        'audit', coalesce((select jsonb_agg(jsonb_build_object('at', x.at, 'action', x.action, 'actor', x.actor_subject, 'detail', x.detail) order by x.at)
                           from exm.audit x where x.request_id = v.id), '[]')));
end $$;

-- p_kind: 'response' (answer) or 'more_information' (asks the owner; status -> more_information_needed).
create or replace function public.exm_staff_respond(p_id uuid, p_body text, p_kind text default 'response') returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    v exm.requests;
    v_name text;
begin
    select * into v_caller from exm.require('support.staff');
    select * into v from exm.requests r where r.id = p_id and r.status <> 'draft' for update;
    if v.id is null then perform exm.fail(404, 'not_found', 'No such request.'); end if;
    if p_kind not in ('response', 'more_information') then perform exm.fail(400, 'invalid', 'kind must be response or more_information.'); end if;
    if char_length(coalesce(trim(p_body), '')) not between 1 and 4000 then perform exm.fail(400, 'invalid', 'Messages are 1–4000 characters.'); end if;
    select coalesce(u.raw_user_meta_data ->> 'display_name', u.raw_user_meta_data ->> 'full_name', 'HCG Support') into v_name
      from auth.users u where u.id = v_caller.subject;
    insert into exm.messages (request_id, author_subject, from_staff, author_name, body, kind)
    values (v.id, v_caller.subject, true, v_name, trim(p_body), p_kind);
    update exm.requests set updated_at = now(),
        status = case when p_kind = 'more_information' then 'more_information_needed'
                      when status = 'submitted' then 'under_review' else status end where id = v.id;
    perform exm.audit_log(v_caller.subject, v_caller.channel, 'staff_' || p_kind, v.id, null);
    return public.exm_staff_get(v.id);
end $$;

create or replace function public.exm_staff_set_status(p_id uuid, p_status text, p_resolution text default null, p_note text default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    v exm.requests;
    v_retention int := (exm.setting('attachment_retention_days') #>> '{}')::int;
begin
    select * into v_caller from exm.require('support.staff');
    select * into v from exm.requests r where r.id = p_id and r.status <> 'draft' for update;
    if v.id is null then perform exm.fail(404, 'not_found', 'No such request.'); end if;
    if p_status not in ('submitted', 'under_review', 'more_information_needed', 'in_progress', 'completed', 'closed') then
        perform exm.fail(400, 'invalid', 'Unknown status.');
    end if;
    if p_resolution is not null and p_resolution not in ('resolved', 'not_planned', 'duplicate') then
        perform exm.fail(400, 'invalid', 'Unknown resolution.');
    end if;
    update exm.requests set status = p_status, updated_at = now(),
        resolution = case when p_status in ('completed', 'closed') then coalesce(p_resolution, resolution, 'resolved') else null end,
        closed_at = case when p_status in ('completed', 'closed') then now() else null end where id = v.id;
    -- Retention clock for attachments starts when the request is completed or closed.
    update exm.attachments set delete_after = case when p_status in ('completed', 'closed') then now() + make_interval(days => v_retention) else null end
     where request_id = v.id and state <> 'deleted';
    insert into exm.messages (request_id, author_subject, from_staff, author_name, body, kind)
    values (v.id, v_caller.subject, true, 'HCG Support', coalesce(nullif(trim(p_note), ''), 'Status changed to ' || replace(p_status, '_', ' ') || '.'), 'status');
    perform exm.audit_log(v_caller.subject, v_caller.channel, 'status_changed', v.id, null,
        jsonb_build_object('from', v.status, 'to', p_status, 'resolution', p_resolution));
    return public.exm_staff_get(v.id);
end $$;

-- Associates the request with a framework/build-adapter development task (an ERP task reference or URL). It does NOT
-- change what HCG Executable Maker supports.
create or replace function public.exm_staff_link_task(p_id uuid, p_task_ref text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    v exm.requests;
begin
    select * into v_caller from exm.require('support.staff');
    select * into v from exm.requests r where r.id = p_id and r.status <> 'draft' for update;
    if v.id is null then perform exm.fail(404, 'not_found', 'No such request.'); end if;
    if p_task_ref is not null and (char_length(p_task_ref) > 300 or p_task_ref ~ '[<>"\s]{2,}') then perform exm.fail(400, 'invalid', 'Invalid task reference.'); end if;
    update exm.requests set dev_task_ref = nullif(trim(p_task_ref), ''), updated_at = now() where id = v.id;
    perform exm.audit_log(v_caller.subject, v_caller.channel, 'task_linked', v.id, null, jsonb_build_object('from', v.dev_task_ref, 'to', p_task_ref));
    return public.exm_staff_get(v.id);
end $$;

-- Staff access to one attachment: audited, then the website route inspects it and issues a 5-minute signed link.
create or replace function public.exm_staff_attachment_target(p_id uuid, p_attachment uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    a exm.attachments;
begin
    select * into v_caller from exm.require('support.staff');
    select x.* into a from exm.attachments x join exm.requests r on r.id = x.request_id
     where x.id = p_attachment and x.request_id = p_id and r.status <> 'draft' and x.state = 'received';
    if a.id is null then perform exm.fail(404, 'not_found', 'No such attachment.'); end if;
    perform exm.audit_log(v_caller.subject, v_caller.channel, 'attachment_accessed', p_id, a.id);
    return jsonb_build_object('path', a.object_path, 'name', a.name, 'sha256', a.declared_sha256, 'contentType', a.content_type, 'scan', a.scan);
end $$;

-- Retention: marks attachments past delete_after as deleted and returns their paths for the website route to remove
-- (staff delete policy). Run from the ERP page (or a scheduled job with a staff session).
create or replace function public.exm_staff_purge_expired() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    v_paths jsonb;
begin
    select * into v_caller from exm.require('support.staff');
    select coalesce(jsonb_agg(a.object_path), '[]') into v_paths from exm.attachments a
     where a.state in ('received', 'rejected', 'pending') and a.delete_after is not null and a.delete_after <= now();
    update exm.attachments set state = 'deleted', deleted_at = now()
     where state in ('received', 'rejected', 'pending') and delete_after is not null and delete_after <= now();
    perform exm.audit_log(v_caller.subject, v_caller.channel, 'retention_purge', null, null, jsonb_build_object('objects', jsonb_array_length(v_paths)));
    return jsonb_build_object('deletePaths', v_paths);
end $$;

grant usage on schema exm to authenticated;
grant execute on function public.exm_access_directory(text), public.exm_support_create(jsonb, text), public.exm_support_list(),
    public.exm_support_get(uuid), public.exm_support_upload_target(uuid, uuid), public.exm_support_complete_attachment(uuid, uuid), public.exm_support_reject_attachment(uuid, uuid, text),
    public.exm_support_submit(uuid), public.exm_support_reply(uuid, text), public.exm_support_withdraw(uuid),
    public.exm_support_download_target(uuid, uuid), public.exm_staff_list(text), public.exm_staff_get(uuid),
    public.exm_staff_respond(uuid, text, text), public.exm_staff_set_status(uuid, text, text, text), public.exm_staff_link_task(uuid, text),
    public.exm_staff_attachment_target(uuid, uuid), public.exm_staff_purge_expired() to authenticated;
revoke execute on all functions in schema exm from public, anon;

-- ---------------------------------------------------------------------------------------------------------------------
-- Storage: private bucket and its policies (no public reads; uploads only to the caller's pending attachments)
-- ---------------------------------------------------------------------------------------------------------------------

insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('executable-maker-support', 'executable-maker-support', false, 52428800,
        array['application/zip', 'text/plain', 'application/json', 'application/octet-stream'])
on conflict (id) do update set public = false, file_size_limit = excluded.file_size_limit, allowed_mime_types = excluded.allowed_mime_types;

create or replace function exm.may_upload(p_name text) returns boolean
language plpgsql stable security definer set search_path = '' as $$
declare
    v_sub text := auth.jwt() ->> 'sub';
begin
    return exists (select 1 from exm.attachments a join exm.requests r on r.id = a.request_id
                   where a.object_path = p_name and a.state = 'pending' and r.status = 'draft' and r.owner_subject::text = v_sub);
end $$;

create or replace function exm.may_read(p_name text) returns boolean
language plpgsql stable security definer set search_path = '' as $$
declare
    v_claims jsonb := auth.jwt();
    v_sub text := v_claims ->> 'sub';
    v_staff boolean := false;
begin
    if v_sub is null or not exm.is_uuid(v_sub) then return false; end if;
    -- Owner: any of their own objects (deleted ones stay visible only until removed: storage removal reads before deleting).
    if exists (select 1 from exm.attachments a where a.object_path = p_name and a.owner_subject::text = v_sub) then
        return true;
    end if;
    if coalesce(v_claims ->> 'client_id', '') = '' then
        v_staff := 'support.staff' = any (exm.permissions(v_sub::uuid, 'website'));
    end if;
    return v_staff and exists (select 1 from exm.attachments a where a.object_path = p_name and a.state in ('received', 'deleted'));
end $$;

create or replace function exm.may_delete(p_name text) returns boolean
language plpgsql stable security definer set search_path = '' as $$
declare
    v_claims jsonb := auth.jwt();
    v_sub text := v_claims ->> 'sub';
begin
    if v_sub is null or not exm.is_uuid(v_sub) then return false; end if;
    return exists (select 1 from exm.attachments a where a.object_path = p_name and a.state = 'deleted'
                   and (a.owner_subject::text = v_sub
                        or (coalesce(v_claims ->> 'client_id', '') = '' and 'support.staff' = any (exm.permissions(v_sub::uuid, 'website')))));
end $$;

grant execute on function exm.may_upload(text), exm.may_read(text), exm.may_delete(text) to authenticated;

drop policy if exists "exm support upload" on storage.objects;
create policy "exm support upload" on storage.objects for insert to authenticated
    with check (bucket_id = 'executable-maker-support' and exm.may_upload(name));
drop policy if exists "exm support read" on storage.objects;
create policy "exm support read" on storage.objects for select to authenticated
    using (bucket_id = 'executable-maker-support' and exm.may_read(name));
drop policy if exists "exm support delete" on storage.objects;
create policy "exm support delete" on storage.objects for delete to authenticated
    using (bucket_id = 'executable-maker-support' and exm.may_delete(name));
