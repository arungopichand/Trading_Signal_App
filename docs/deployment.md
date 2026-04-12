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
- `ENABLE_FINNHUB_PRICE_STREAM=true`

### Commands
- Build: `dotnet publish -c Release -o out`
- Start: `dotnet out/SignalFeed.Api.dll`

## CI/CD
Active workflows:
- `.github/workflows/build.yml` (PR validation for `dev` and `main`)
- `.github/workflows/deploy.yml` (production deploy on `main`)

Production workflow:
- build + lint + test
- deploy frontend to Vercel
- poll backend health endpoint for readiness
- smoke test `/api/feed?limit=1`

## GitHub Action secrets
- `VERCEL_TOKEN`
- `VERCEL_ORG_ID`
- `VERCEL_PROJECT_ID`
- `BACKEND_HEALTHCHECK_URL`

## Branch strategy
- `feature/* -> dev -> main`
- no direct push to `dev` or `main` (enforce through GitHub branch protection)

## Health Endpoints
- `GET /health`
- `GET /health/stream`
