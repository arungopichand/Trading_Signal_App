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
Active workflows:
- `.github/workflows/build.yml` on pull requests to `dev` or `main`
- `.github/workflows/deploy.yml` on push to `main` (production)

Production deploy flow:
- Build + lint + tests
- Deploy frontend to Vercel
- Verify backend readiness via `BACKEND_HEALTHCHECK_URL/health`
- Run backend smoke check on `/api/feed?limit=1`

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
- `BACKEND_HEALTHCHECK_URL` (example: `https://trading-backend-prod.onrender.com`)

## Documentation
- [Deployment](docs/deployment.md)
- [Architecture](docs/architecture.md)
- [API](docs/api.md)
