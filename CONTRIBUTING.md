# Contributing Guide

## Branch Strategy
- `main` -> production
- `dev` -> development
- `feature/*` -> feature branches

## Pull Request Rules
1. `feature/*` branches must target `dev`.
2. `dev` promotion must go through a PR into `main`.
3. CI must pass before merge.
4. Do not merge if tests fail.

## Required GitHub Branch Protection
1. Block direct pushes to `main`.
2. Block direct pushes to `dev`.
3. Require pull request reviews.
4. Require status checks to pass:
   - `Build and Test / frontend`
   - `Build and Test / backend`

## Local Validation
- `dotnet restore Trading_Signal_App.sln`
- `dotnet build Trading_Signal_App.sln -c Release`
- `dotnet test backend/SignalFeed.Tests/SignalFeed.Tests.csproj -c Release`
- `cd frontend && npm ci && npm run lint && npm run build`

## Security Rules
1. Never commit secrets or `.env` files.
2. Keep privileged keys in GitHub/Render/Vercel secret stores only.
3. Rotate leaked credentials immediately.
