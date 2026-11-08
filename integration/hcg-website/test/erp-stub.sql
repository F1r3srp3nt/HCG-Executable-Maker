-- Stand-in for the ERP's App Access tables and function, shaped like the website's ERP uses them (identified from its
-- public bundle by the LifeOS integration; confirm columns before deploying). Only for local tests.
create table public.app_applications (id uuid primary key default gen_random_uuid(), product_id text unique not null, name text not null);
create table public.app_entitlements (
    id uuid primary key default gen_random_uuid(), employee_id uuid not null, application_id uuid not null references public.app_applications(id),
    plan_id uuid, status text not null default 'active', features jsonb not null default '{}', granted_at timestamptz default now(),
    expires_at timestamptz, revoked_at timestamptz, unique (employee_id, application_id));
create function public.app_effective_entitlements(_user uuid)
returns table (product_id text, status text, source text, features jsonb, expires_at timestamptz)
language sql stable as $$
    select a.product_id, e.status, 'entitlement', e.features, e.expires_at
    from public.app_entitlements e join public.app_applications a on a.id = e.application_id
    where e.employee_id = _user and e.status = 'active' and e.revoked_at is null and (e.expires_at is null or e.expires_at > now())
$$;
create table public.user_roles (user_id uuid not null, role text not null);
insert into public.app_applications (product_id, name) values
    ('executable-maker', 'HCG Executable Maker'), ('executable-maker-support-desk', 'HCG Executable Maker support desk'), ('lifeos', 'LifeOS');
