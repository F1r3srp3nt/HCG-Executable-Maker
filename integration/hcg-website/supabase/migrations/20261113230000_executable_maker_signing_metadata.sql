-- HCG Executable Maker — signing metadata (HCG Signing Vault, account integration step 3).
--
-- Stores NON-SECRET metadata only: certificate fingerprints, team ID, certificate type, expiry; provisioning profile
-- UUID, name, App ID pattern, type, platforms, expiry; and per-project signing preferences (references only).
-- Private keys, .p12 files, passwords, profile files and device UDIDs are never accepted (see docs/SIGNING-VAULT.md
-- for the separate encrypted-vault design, which is NOT implemented here).
--
-- Authorization: the caller's own token (exm.caller) through the desktop app's registered client, and ERP App Access
-- for product `executable-maker`. Tenant isolation: every row belongs to exactly one HCG account (owner_subject);
-- RLS is enabled with no policies, so the tables are reachable only through these SECURITY DEFINER functions, which
-- always filter by the caller. Another account's records answer 404 (existence is not revealed).
-- Depends on 20261108200000_executable_maker_support.sql (schema exm, exm.caller, exm.entitlement, exm.fail, …).

create table if not exists exm.signing_assets (
    id uuid primary key default gen_random_uuid(),
    owner_subject uuid not null,
    asset_type text not null check (asset_type in ('certificate', 'profile')),
    sha1 text check (sha1 ~ '^[0-9A-F]{40}$'),
    sha256 text check (sha256 ~ '^[0-9A-F]{64}$'),
    profile_uuid uuid,
    display_name text check (char_length(display_name) <= 200),
    team_id text check (team_id ~ '^[A-Z0-9]{10}$'),
    certificate_kind text check (certificate_kind in ('DeveloperIdApplication', 'DeveloperIdInstaller', 'AppleDistribution', 'MacAppDistribution',
                                                      'MacInstallerDistribution', 'Development', 'Other')),
    profile_kind text check (profile_kind in ('Development', 'AdHoc', 'AppStore', 'Enterprise', 'DeveloperId', 'Unknown')),
    bundle_id_pattern text check (bundle_id_pattern ~ '^[A-Za-z0-9.*-]{1,200}$'),
    platforms text[] not null default '{}',
    expires_at timestamptz,
    device_id uuid not null,
    device_label text check (char_length(device_label) <= 100),
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    check ((asset_type = 'certificate' and sha1 is not null and profile_uuid is null)
        or (asset_type = 'profile' and profile_uuid is not null and sha1 is null and sha256 is null))
);
create unique index if not exists signing_assets_unique_cert on exm.signing_assets (owner_subject, sha1, device_id) where asset_type = 'certificate';
create unique index if not exists signing_assets_unique_profile on exm.signing_assets (owner_subject, profile_uuid, device_id) where asset_type = 'profile';
create index if not exists signing_assets_owner on exm.signing_assets (owner_subject);

create table if not exists exm.signing_project_preferences (
    owner_subject uuid not null,
    project_key text not null check (project_key ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$'),
    preferences jsonb not null,
    updated_at timestamptz not null default now(),
    primary key (owner_subject, project_key)
);

alter table exm.signing_assets enable row level security;
alter table exm.signing_project_preferences enable row level security;
revoke all on exm.signing_assets, exm.signing_project_preferences from public, anon, authenticated;

-- The caller must use the desktop app with ERP App Access to Executable Maker; revoked access answers 403 revoked.
create or replace function exm.require_signing(out subject uuid)
language plpgsql security definer set search_path = '' as $$
declare
    v_caller record;
    v_state record;
begin
    select * into v_caller from exm.caller();
    if v_caller.channel <> 'app' then perform exm.fail(403, 'forbidden', 'This API is for HCG Executable Maker.'); end if;
    select * into v_state from exm.entitlement(v_caller.subject, exm.setting('app_product') #>> '{}');
    if v_state.state = 'revoked' then perform exm.fail(403, 'revoked', 'HCG has withdrawn HCG Executable Maker access for this account.'); end if;
    if v_state.state <> 'granted' then perform exm.fail(403, 'forbidden', 'Your HCG account does not have this permission (ERP > App Access).'); end if;
    subject := v_caller.subject;
end $$;

create or replace function exm.signing_asset_json(a exm.signing_assets) returns jsonb
language sql stable security definer set search_path = '' as $$
    select jsonb_strip_nulls(jsonb_build_object(
        'id', a.id, 'assetType', a.asset_type, 'sha1', a.sha1, 'sha256', a.sha256, 'profileUuid', a.profile_uuid,
        'displayName', a.display_name, 'teamId', a.team_id, 'certificateKind', a.certificate_kind, 'profileKind', a.profile_kind,
        'bundleIdPattern', a.bundle_id_pattern, 'platforms', to_jsonb(a.platforms), 'expiresAt', a.expires_at,
        'deviceId', a.device_id, 'deviceLabel', a.device_label, 'updatedAt', a.updated_at))
$$;

-- GET /signing/assets
create or replace function public.exm_signing_list() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_subject uuid;
begin
    select subject into v_subject from exm.require_signing();
    return jsonb_build_object('assets', coalesce((select jsonb_agg(exm.signing_asset_json(a) order by a.asset_type, a.display_name, a.id)
        from exm.signing_assets a where a.owner_subject = v_subject), '[]'));
end $$;

-- POST /signing/assets — idempotent per (owner, fingerprint or profile UUID, device). Only allow-listed keys are read;
-- anything else in the body (a key, a password, a file) is refused rather than ignored.
create or replace function public.exm_signing_record(p_asset jsonb) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_subject uuid;
    v_row exm.signing_assets;
    v_allowed text[] := array['assetType', 'sha1', 'sha256', 'profileUuid', 'displayName', 'teamId', 'certificateKind', 'profileKind',
                              'bundleIdPattern', 'platforms', 'expiresAt', 'deviceId', 'deviceLabel'];
    v_key text;
    v_platforms text[];
begin
    select subject into v_subject from exm.require_signing();
    if jsonb_typeof(p_asset) <> 'object' then perform exm.fail(400, 'invalid', 'Send one asset record.'); end if;
    for v_key in select jsonb_object_keys(p_asset) loop
        if not (v_key = any (v_allowed)) then
            perform exm.fail(400, 'invalid', format('"%s" is not accepted. Only public certificate and profile details can be recorded; private keys and passwords are never uploaded.', left(v_key, 40)));
        end if;
    end loop;
    if octet_length(p_asset::text) > 4096 then perform exm.fail(413, 'too_large', 'An asset record is at most 4 KB.'); end if;
    if not exm.is_uuid(p_asset ->> 'deviceId') then perform exm.fail(400, 'invalid', 'deviceId must be a UUID.'); end if;
    if (p_asset ->> 'assetType') = 'profile' and not exm.is_uuid(p_asset ->> 'profileUuid') then perform exm.fail(400, 'invalid', 'profileUuid must be a UUID.'); end if;
    select coalesce(array_agg(x), '{}') into v_platforms from jsonb_array_elements_text(coalesce(p_asset -> 'platforms', '[]')) x;
    if exists (select 1 from unnest(v_platforms) p where p not in ('iOS', 'OSX', 'xrOS', 'tvOS', 'watchOS')) then
        perform exm.fail(400, 'invalid', 'Unknown platform.');
    end if;
    begin
        insert into exm.signing_assets as a (owner_subject, asset_type, sha1, sha256, profile_uuid, display_name, team_id, certificate_kind, profile_kind,
                                             bundle_id_pattern, platforms, expires_at, device_id, device_label)
        values (v_subject, p_asset ->> 'assetType', upper(p_asset ->> 'sha1'), upper(p_asset ->> 'sha256'), (p_asset ->> 'profileUuid')::uuid,
                p_asset ->> 'displayName', p_asset ->> 'teamId', p_asset ->> 'certificateKind', p_asset ->> 'profileKind', p_asset ->> 'bundleIdPattern',
                v_platforms, (p_asset ->> 'expiresAt')::timestamptz, (p_asset ->> 'deviceId')::uuid, left(p_asset ->> 'deviceLabel', 100))
        on conflict (owner_subject, sha1, device_id) where asset_type = 'certificate'
            do update set display_name = excluded.display_name, team_id = excluded.team_id, certificate_kind = excluded.certificate_kind,
                          sha256 = excluded.sha256, expires_at = excluded.expires_at, device_label = excluded.device_label, updated_at = now()
        returning * into v_row;
    exception
        when unique_violation then
            update exm.signing_assets a set display_name = p_asset ->> 'displayName', team_id = p_asset ->> 'teamId', profile_kind = p_asset ->> 'profileKind',
                   bundle_id_pattern = p_asset ->> 'bundleIdPattern', platforms = v_platforms, expires_at = (p_asset ->> 'expiresAt')::timestamptz,
                   device_label = left(p_asset ->> 'deviceLabel', 100), updated_at = now()
             where a.owner_subject = v_subject and a.asset_type = 'profile' and a.profile_uuid = (p_asset ->> 'profileUuid')::uuid
               and a.device_id = (p_asset ->> 'deviceId')::uuid
            returning * into v_row;
        when check_violation or invalid_text_representation or datetime_field_overflow or invalid_datetime_format then
            perform exm.fail(400, 'invalid', 'The record has an invalid fingerprint, UUID, team ID, type or date.');
    end;
    perform exm.audit_log(v_subject, 'app', 'signing_asset_recorded', null, null, jsonb_build_object('assetType', v_row.asset_type, 'id', v_row.id));
    return jsonb_build_object('asset', exm.signing_asset_json(v_row));
end $$;

-- DELETE /signing/assets/{id} — removes HCG's record only (the keychain, other Macs and Apple are not affected).
create or replace function public.exm_signing_forget(p_id uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_subject uuid;
    v_deleted uuid;
begin
    select subject into v_subject from exm.require_signing();
    delete from exm.signing_assets a where a.id = p_id and a.owner_subject = v_subject returning a.id into v_deleted;
    if v_deleted is null then perform exm.fail(404, 'not_found', 'No such record.'); end if;
    perform exm.audit_log(v_subject, 'app', 'signing_asset_forgotten', null, null, jsonb_build_object('id', p_id));
    return jsonb_build_object('removed', true);
end $$;

-- GET /signing/projects/{key}
create or replace function public.exm_signing_project_get(p_project text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_subject uuid;
begin
    select subject into v_subject from exm.require_signing();
    return jsonb_build_object('preferences', (select p.preferences from exm.signing_project_preferences p
                                               where p.owner_subject = v_subject and p.project_key = p_project));
end $$;

-- PUT /signing/projects/{key} — references only (team, mode, route, certificate SHA-1, profile UUIDs, target names).
create or replace function public.exm_signing_project_put(p_project text, p_preferences jsonb) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
    v_subject uuid;
    v_key text;
    v_target jsonb;
    v_clean jsonb;
begin
    select subject into v_subject from exm.require_signing();
    if p_project is null or p_project !~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$' then perform exm.fail(400, 'invalid', 'Invalid project key.'); end if;
    if jsonb_typeof(p_preferences) <> 'object' or octet_length(p_preferences::text) > 16384 then perform exm.fail(400, 'invalid', 'Send the preferences object (at most 16 KB).'); end if;
    for v_key in select jsonb_object_keys(p_preferences) loop
        if v_key not in ('platform', 'mode', 'route', 'teamId', 'identitySha1', 'installerIdentitySha1', 'profileUuid', 'targets') then
            perform exm.fail(400, 'invalid', format('"%s" is not a signing preference.', left(v_key, 40)));
        end if;
    end loop;
    if coalesce(p_preferences ->> 'teamId', 'ABCDE12345') !~ '^[A-Z0-9]{10}$'
       or coalesce(p_preferences ->> 'identitySha1', repeat('0', 40)) !~ '^[0-9A-Fa-f]{40}$'
       or coalesce(p_preferences ->> 'installerIdentitySha1', repeat('0', 40)) !~ '^[0-9A-Fa-f]{40}$'
       or not exm.is_uuid(coalesce(p_preferences ->> 'profileUuid', '00000000-0000-0000-0000-000000000000'))
       or coalesce(p_preferences ->> 'platform', 'Ios') not in ('Ios', 'MacOs')
       or coalesce(p_preferences ->> 'mode', 'Manual') not in ('XcodeAutomatic', 'Manual')
       or coalesce(p_preferences ->> 'route', 'LocalDevelopment') not in ('LocalDevelopment', 'IosAdHoc', 'IosAppStore', 'IosEnterprise', 'MacDeveloperId', 'MacAppStore') then
        perform exm.fail(400, 'invalid', 'A preference has an invalid value.');
    end if;
    if jsonb_typeof(coalesce(p_preferences -> 'targets', '[]')) <> 'array' or jsonb_array_length(coalesce(p_preferences -> 'targets', '[]')) > 50 then
        perform exm.fail(400, 'invalid', 'targets must be a list of at most 50.');
    end if;
    for v_target in select * from jsonb_array_elements(coalesce(p_preferences -> 'targets', '[]')) loop
        if jsonb_typeof(v_target) <> 'object' or exists (select 1 from jsonb_object_keys(v_target) k where k not in ('targetName', 'bundleIdentifier', 'profileUuid'))
           or not exm.is_uuid(coalesce(v_target ->> 'profileUuid', '00000000-0000-0000-0000-000000000000')) then
            perform exm.fail(400, 'invalid', 'A target may carry only targetName, bundleIdentifier and profileUuid.');
        end if;
    end loop;
    v_clean := jsonb_strip_nulls(p_preferences);
    insert into exm.signing_project_preferences as p (owner_subject, project_key, preferences) values (v_subject, p_project, v_clean)
    on conflict (owner_subject, project_key) do update set preferences = excluded.preferences, updated_at = now();
    return jsonb_build_object('preferences', v_clean);
end $$;

revoke all on function exm.require_signing(), exm.signing_asset_json(exm.signing_assets) from public, anon, authenticated;
revoke all on function public.exm_signing_list(), public.exm_signing_record(jsonb), public.exm_signing_forget(uuid),
    public.exm_signing_project_get(text), public.exm_signing_project_put(text, jsonb) from public, anon;
grant execute on function public.exm_signing_list(), public.exm_signing_record(jsonb), public.exm_signing_forget(uuid),
    public.exm_signing_project_get(text), public.exm_signing_project_put(text, jsonb) to authenticated;
