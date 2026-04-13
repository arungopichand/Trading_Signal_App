# Contributing Guide

## Branch Strategy
- `main` -> production only
- `dev` -> integration only
- `feature/*` -> developer feature branches (must branch from `dev`)

## Pull Request Rules
1. `feature/*` branches must target `dev`.
2. `dev` promotion must go through a PR into `main`.
3. CI must pass before merge.
4. Do not merge if tests fail.
5. Production deploys happen only from `main` (`.github/workflows/deploy.yml`).
6. No direct push to `dev`.
7. No direct push to `main`.

## Required GitHub Branch Protection
1. Block direct pushes to `main`.
2. Block direct pushes to `dev`.
3. Require pull request reviews.
4. Require status checks to pass:
   - `Build and Test / frontend`
   - `Build and Test / backend`
5. Restrict merge targets:
   - `feature/*` -> `dev` only
   - `dev` -> `main` only for production promotion

## Local Validation
- `dotnet restore Trading_Signal_App.sln`
- `dotnet build Trading_Signal_App.sln -c Release`
- `dotnet test backend/SignalFeed.Tests/SignalFeed.Tests.csproj -c Release`
- `cd frontend && npm ci && npm run lint && npm run build`
- Validate backend endpoints locally:
  - `GET http://localhost:10000/health`
  - `GET http://localhost:10000/health/stream`
  - `GET http://localhost:10000/api/feed?limit=1`

## Security Rules
1. Never commit secrets or `.env` files.
2. Keep privileged keys in GitHub/Render/Vercel secret stores only.
3. Rotate leaked credentials immediately.
