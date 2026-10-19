-- Run once in the Supabase SQL editor. Only server-side service-role/secret keys may access this table.
create table if not exists public.forex_state (
    id text primary key,
    payload jsonb not null,
    updated_at timestamptz not null default now()
);

alter table public.forex_state enable row level security;
revoke all on public.forex_state from anon, authenticated;
grant select, insert, update on public.forex_state to service_role;

create or replace function public.forex_state_touch_updated_at()
returns trigger language plpgsql set search_path = public as $$
begin
    new.updated_at = now();
    return new;
end;
$$;

drop trigger if exists forex_state_updated_at on public.forex_state;
create trigger forex_state_updated_at before update on public.forex_state
for each row execute function public.forex_state_touch_updated_at();
