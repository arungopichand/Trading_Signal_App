# Deployment Guide

## Environments
- **Staging**: branch `dev`
- **Production**: branch `main`

## Frontend (Vercel)
### Mapping
- `main` -> Production deployment
- `dev` -> Preview deployment

### Required GitHub Secrets
- `VERCEL_TOKEN`
- `VERCEL_ORG_ID`
- `VERCEL_PROJECT_ID`

### Required Vercel Environment Variables
- `VITE_API_BASE_URL`
- `VITE_SIGNALR_HUB_URL`

Set in Vercel per environment:
1. Preview values for staging backend URL
2. Production values for production backend URL

## Backend (Render)
### Services
- `trading-backend-staging` bound to `dev`
- `trading-backend-prod` bound to `main`

### Required GitHub Secrets
- `RENDER_STAGING_DEPLOY_HOOK_URL`
- `RENDER_PRODUCTION_DEPLOY_HOOK_URL`

### Backend Env Vars (Render)
- `FINNHUB__APIKEY`
- `POLYGON__APIKEY`
- `NEWSAPI__APIKEY`
- `FMP__APIKEY`
- `SUPABASE_URL`
- `SUPABASE_KEY`
- `CORS_ALLOWED_ORIGINS`

## CI/CD Trigger Rules
1. PR to `dev`: quality gate only.
2. Push/merge to `dev`: deploy staging.
3. Push/merge to `main`: deploy production.

## Rollback
### Vercel
1. Open Vercel project deployments.
2. Promote previous healthy deployment to production.

### Render
1. Open Render service events.
2. Roll back to last successful deploy.
3. If needed, trigger deploy hook for a known-good commit.

### Git
1. Revert bad commit on environment branch:
   - `git revert <commit_sha>`
2. Push revert and let pipeline redeploy.
