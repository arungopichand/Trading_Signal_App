# Trading Signal App

Real-time stock intelligence platform with a clean split deployment architecture:
- Frontend (`/frontend`) on Vercel
- Backend (`/backend/SignalFeed.Api`) on Render
- Database/Auth on Supabase

## Repository Layout
```text
root/
|-- frontend/
|-- backend/SignalFeed.Api/
|-- .github/workflows/
|-- docs/
|-- .env.example
|-- render.yaml
|-- Trading_Signal_App.sln
```

## Branching Model
- `feature/*` -> `dev` -> `main`
- `dev` = development environment
- `main` = production environment

## CI/CD Workflows
Only two workflows are used:
- `.github/workflows/dev-deploy.yml` on push to `dev`
- `.github/workflows/prod-deploy.yml` on push to `main`

Each workflow:
- detects changed areas (frontend/backend/supabase)
- builds only changed parts
- deploys only changed parts

## Local Setup
1. Copy `.env.example` to `.env` and fill values.
2. Backend:
   - `dotnet restore Trading_Signal_App.sln`
   - `dotnet run --project backend/SignalFeed.Api/SignalFeed.Api.csproj`
3. Frontend:
   - `cd frontend`
   - `npm ci`
   - `npm run dev`

## Required Secrets (GitHub Actions)
- `VERCEL_TOKEN`
- `VERCEL_ORG_ID`
- `VERCEL_PROJECT_ID`
- `RENDER_STAGING_DEPLOY_HOOK_URL`
- `RENDER_PRODUCTION_DEPLOY_HOOK_URL`
- `SUPABASE_ACCESS_TOKEN`
- `SUPABASE_DEV_PROJECT_REF`
- `SUPABASE_DEV_DB_PASSWORD`
- `SUPABASE_PROD_PROJECT_REF`
- `SUPABASE_PROD_DB_PASSWORD`

## Documentation
- [Deployment](docs/deployment.md)
- [Architecture](docs/architecture.md)
- [API](docs/api.md)