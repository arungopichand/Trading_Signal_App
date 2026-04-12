create extension if not exists pgcrypto;

create table if not exists public.tracked_symbols (
  id uuid primary key default gen_random_uuid(),
  symbol text not null unique,
  is_active boolean not null default true,
  created_at timestamptz not null default now()
);

create index if not exists idx_tracked_symbols_active_symbol
  on public.tracked_symbols (is_active, symbol);
