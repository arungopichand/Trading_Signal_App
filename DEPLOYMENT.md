# Deployment Guide

## Architecture
- Frontend: Vercel (`frontend/`)
- Backend: Render (`backend/SignalFeed.Api`)
- Data/Auth: Supabase

Flow: `Frontend (Vercel) -> Backend API (Render) -> Supabase`

## CI/CD Workflows

### 1. PR Validation
- File: `.github/workflows/build.yml`
- Trigger:
  - `push` to `feature/*` (CI only)
  - `pull_request` to `dev` or `main`
- Actions:
  - Build frontend with Node 24
  - Lint frontend
  - Build backend with .NET
  - Run backend tests

### 2. Development Deploy
- File: `.github/workflows/dev-deploy.yml`
- Trigger: `push` to `dev` only
- Actions:
  - Build/test frontend + backend
  - Frontend preview deploy to Vercel from `frontend/` only
  - Backend dev deploy via Render dev hook
  - Backend dev health check
- Guardrails:
  - Never runs on `main` or `feature/*`
  - Never uses production Vercel deploy flags (`--prod`)

### 3. Production Deploy
- File: `.github/workflows/deploy.yml`
- Trigger: `push` to `main` only
- Actions:
  - Build frontend + backend
  - Run tests (deploy jobs require successful build/test)
  - Deploy frontend to Vercel production from `frontend/` only
  - Trigger backend deploy via Render deploy hook
  - Retry backend checks up to 10 attempts with 10s delay:
    - `/health`
    - `/api/feed?limit=1`
    - optional `/health/stream`

Pipeline fails if any build/test/deploy/health step fails.

## Required GitHub Secrets

### Vercel
- `VERCEL_TOKEN`
- `VERCEL_ORG_ID`
- `VERCEL_FRONTEND_PROJECT_ID` (recommended)
  - Legacy fallback supported: `VERCEL_PROJECT_ID`

### Render
- `BACKEND_HEALTHCHECK_URL`
  - Example: `https://your-backend-service.onrender.com`
  - Workflow checks: `${BACKEND_HEALTHCHECK_URL}/health`
- `RENDER_DEPLOY_HOOK`
  - Example: `https://api.render.com/deploy/srv-xxxx?key=yyyy`
  - Used by production workflow to trigger backend deploy explicitly

## Deploy Commands Used in CI

### Frontend
```bash
npx vercel pull --yes --environment=preview --token "$VERCEL_TOKEN" --cwd frontend
npx vercel build --token "$VERCEL_TOKEN" --cwd frontend
npx vercel deploy --prebuilt --yes --token "$VERCEL_TOKEN" --cwd frontend
```

```bash
npx vercel pull --yes --environment=production --token "$VERCEL_TOKEN" --cwd frontend
npx vercel build --prod --token "$VERCEL_TOKEN" --cwd frontend
npx vercel deploy --prebuilt --prod --yes --token "$VERCEL_TOKEN" --cwd frontend
```

### Health Check
```bash
curl --fail --show-error --silent "$BACKEND_HEALTHCHECK_URL/health"
curl --fail --show-error --silent "$BACKEND_HEALTHCHECK_URL/api/feed?limit=1"
curl --fail --show-error --silent "$BACKEND_HEALTHCHECK_URL/health/stream" # optional
```

## Render Service Runtime
- Build command: `dotnet publish -c Release -o out`
- Start command: `dotnet out/SignalFeed.Api.dll`
- Health endpoint: `/health`
- Stream health endpoint: `/health/stream`
- Deploy hook handling in CI: try `POST` first, then fallback to `GET` if method mismatch occurs.

## Operational Notes
1. Enable branch protection on `main` and `dev`.
2. Require PR + passing checks before merge.
3. Keep secrets only in GitHub/Vercel/Render secret stores.
4. Vercel project must be the frontend project with **Root Directory = `frontend`**.
5. Never deploy frontend from repo root in CI.
6. Keep Render service `autoDeployTrigger=commit` for `main`.
7. Rollback: redeploy previous stable commit SHA in Render and re-run health checks.
