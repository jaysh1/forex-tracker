-- Run once in Supabase: SQL Editor > New query > paste > Run.
create table if not exists public.bot_state (
  key        text primary key,
  value      jsonb not null,
  updated_at timestamptz not null default now()
);
alter table public.bot_state enable row level security;   -- no policies: only the secret key can use the table
revoke all on public.bot_state from anon, authenticated;
grant all on public.bot_state to service_role;
