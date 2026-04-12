# Deployment Guide

## Architecture
- Frontend: Vercel (`frontend/`)
- Backend: Render (`backend/SignalFeed.Api`)
- Data/Auth: Supabase

Flow: `Frontend (Vercel) -> Backend API (Render) -> Supabase`

## CI/CD Workflows

### 1. PR Validation
- File: `.github/workflows/build.yml`
- Trigger: `pull_request` to `dev` or `main`
- Actions:
  - Build frontend with Node 24
  - Lint frontend
  - Build backend with .NET
  - Run backend tests

### 2. Production Deploy
- File: `.github/workflows/deploy.yml`
- Trigger: `push` to `main`
- Actions:
  - Build frontend + backend
  - Run tests (fail-fast)
  - Deploy frontend to Vercel
  - Wait for Render auto-deploy readiness
  - Run backend health check
  - Run backend smoke check (`/api/feed?limit=1`)

Pipeline fails if any build/test/deploy/health step fails.

## Required GitHub Secrets

### Vercel
- `VERCEL_TOKEN`
- `VERCEL_ORG_ID`
- `VERCEL_PROJECT_ID`

### Render
- `BACKEND_HEALTHCHECK_URL`
  - Example: `https://your-backend-service.onrender.com`
  - Workflow checks: `${BACKEND_HEALTHCHECK_URL}/health`

## Deploy Commands Used in CI

### Frontend
```bash
npx vercel deploy --prod --yes --token "$VERCEL_TOKEN"
```

### Health Check
```bash
curl --fail --show-error --silent "$BACKEND_HEALTHCHECK_URL/health"
curl --fail --show-error --silent "$BACKEND_HEALTHCHECK_URL/api/feed?limit=1"
```

## Render Service Runtime
- Build command: `dotnet publish -c Release -o out`
- Start command: `dotnet out/SignalFeed.Api.dll`
- Health endpoint: `/health`
- Stream health endpoint: `/health/stream`

## Operational Notes
1. Enable branch protection on `main` and `dev`.
2. Require PR + passing checks before merge.
3. Keep secrets only in GitHub/Vercel/Render secret stores.
4. Keep Render service `autoDeployTrigger=commit` for `main`.
5. Rollback: redeploy previous stable commit SHA in Render and re-run health checks.
