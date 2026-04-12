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
  - Build frontend with Node 22
  - Lint frontend
  - Build backend with .NET
  - Run backend tests

### 2. Development Deploy
- File: `.github/workflows/dev-deploy.yml`
- Trigger: `push` to `dev`
- Actions:
  - Build frontend + backend
  - Run tests (fail-fast)
  - Deploy frontend preview to Vercel
  - Trigger backend dev deploy on Render
  - Run backend dev health check

### 3. Production Deploy
- File: `.github/workflows/deploy.yml`
- Trigger: `push` to `main`
- Actions:
  - Build frontend + backend
  - Run tests (fail-fast)
  - Deploy frontend to Vercel
  - Trigger backend deploy on Render
  - Run backend health check

Pipeline fails if any build/test/deploy/health step fails.

## Required GitHub Secrets

### Vercel
- `VERCEL_TOKEN`
- `VERCEL_ORG_ID`
- `VERCEL_PROJECT_ID`

### Render
- `RENDER_DEPLOY_HOOK`
- `RENDER_DEV_DEPLOY_HOOK`

### Health Verification
- `BACKEND_HEALTHCHECK_URL`
  - Example: `https://your-backend-service.onrender.com`
  - Workflow checks: `${BACKEND_HEALTHCHECK_URL}/health`
- `BACKEND_DEV_HEALTHCHECK_URL`
  - Example: `https://your-dev-backend-service.onrender.com`
  - Workflow checks: `${BACKEND_DEV_HEALTHCHECK_URL}/health`

## Deploy Commands Used in CI

### Frontend
```bash
npx vercel deploy --prod --yes --token "$VERCEL_TOKEN"
```

### Backend
```bash
curl --fail -X POST "$RENDER_DEPLOY_HOOK"
```

### Health Check
```bash
curl --fail --show-error --silent "$BACKEND_HEALTHCHECK_URL/health"
```

## Render Service Runtime
- Build command: `dotnet publish -c Release -o out`
- Start command: `dotnet out/SignalFeed.Api.dll`
- Health endpoint: `/health`

## Operational Notes
1. Enable branch protection on `main` and `dev`.
2. Require PR + passing checks before merge.
3. Keep secrets only in GitHub/Vercel/Render secret stores.
