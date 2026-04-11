# Auto-Deployment Setup

## Target Flow

- Push to `dev`:
  - GitHub Actions runs CI build checks
  - Vercel creates a preview deployment
- Push to `main`:
  - GitHub Actions runs CI build checks
  - Vercel deploys production frontend
  - Render deploys production backend

After one-time platform setup, no manual deploy commands are needed.

## Repository Layout

- Backend: `backend/SignalFeed.Api`
- Frontend: `signal-ui`

## Branch Model

- `main`: production
- `dev`: development/preview

## GitHub Actions

Workflow file: `.github/workflows/deploy.yml`

- Triggers on push to `main` and `dev`
- Builds frontend (`signal-ui`)
- Builds backend (`backend/SignalFeed.Api`)
- On `main` pushes:
  - Deploys frontend to Vercel (if `VERCEL_TOKEN` is set)
  - Triggers Render deploy hook (if `RENDER_DEPLOY_HOOK_URL` is set)

Required GitHub repository secrets:

- `VERCEL_TOKEN`
- `RENDER_DEPLOY_HOOK_URL`

## Vercel (Frontend)

Project configuration:

1. Import this repository in Vercel.
2. Set Root Directory to `signal-ui`.
3. Framework preset: `Vite`.
4. Production branch: `main`.
5. Preview branch flow: `dev` pushes generate preview deployments.
6. Keep Vercel Git integration connected (for native previews) even though `main` is also deployed by GitHub Actions.

Environment variables (Production + Preview):

- `VITE_API_BASE_URL=https://your-api.onrender.com`
- `VITE_SIGNALR_HUB_URL=https://your-api.onrender.com/hubs/feed`

`signal-ui/vercel.json` already includes Vite build/output settings.

## Render (Backend)

Blueprint file: `render.yaml`

Configured service:

- Root directory: `backend/SignalFeed.Api`
- Build: `dotnet restore && dotnet build --configuration Release --no-restore`
- Start: `dotnet run --urls=http://0.0.0.0:10000`
- Auto deploy: enabled (`autoDeployTrigger: commit`)

Required environment variables on Render:

- `FINNHUB__APIKEY`
- `SUPABASE_URL` (if used)
- `SUPABASE_KEY` (if used)
- `CORS_ALLOWED_ORIGINS` (set to your Vercel URL)

## Daily Workflow

1. Develop on `dev` and push:
   - `git checkout dev`
   - make changes
   - `git push origin dev`
2. Promote to production:
   - `git checkout main`
   - `git merge dev`
   - `git push origin main`
