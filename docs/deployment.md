# Deployment Guide

## Environments
- **Staging**: `dev`
- **Production**: `main`

## Architecture
- Frontend on Vercel from `frontend/`
- Backend on Render from `backend/SignalFeed.Api`
- Supabase used by backend only for privileged database access

## Frontend (Vercel)
### Required variables
- `VITE_API_BASE_URL`
- `VITE_SUPABASE_URL`
- `VITE_SUPABASE_ANON_KEY`

## Backend (Render)
### Required variables
- `SUPABASE_URL`
- `SUPABASE_SERVICE_KEY`
- `CORS_ALLOWED_ORIGINS`
- `FINNHUB__APIKEY`
- `POLYGON__APIKEY`
- `NEWSAPI__APIKEY`
- `FMP__APIKEY`

### Startup controls
- `ENABLE_SIGNAL_SCANNER=true`
- `ENABLE_UNIVERSE_REFRESH=true`
- `ENABLE_REALTIME_STREAM=false`

### Commands
- Build: `dotnet publish -c Release -o out`
- Start: `dotnet out/SignalFeed.Api.dll`

## CI/CD
Only these workflows exist:
- `.github/workflows/dev-deploy.yml`
- `.github/workflows/prod-deploy.yml`

Both workflows:
- detect changed paths
- skip unnecessary builds
- deploy only impacted components

## Supabase migration secrets
- `SUPABASE_ACCESS_TOKEN`
- `SUPABASE_DEV_PROJECT_REF`
- `SUPABASE_DEV_DB_PASSWORD`
- `SUPABASE_PROD_PROJECT_REF`
- `SUPABASE_PROD_DB_PASSWORD`

## Branch strategy
- `feature/* -> dev -> main`
- no direct push to `dev` or `main` (enforce through GitHub branch protection)