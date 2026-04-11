create extension if not exists pgcrypto;

create table if not exists public.tracked_symbols (
  id uuid primary key default gen_random_uuid(),
  symbol text not null unique,
  is_active boolean not null default true,
  created_at timestamptz not null default now()
);
