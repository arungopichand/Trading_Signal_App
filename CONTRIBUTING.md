# Contributing Guide

## Branch Rules
1. Never commit directly to `main`.
2. Never push feature work directly to `dev`.
3. Always branch from `dev`:
   - `feature/<name>`
   - `bugfix/<name>`
   - `hotfix/<name>`

## Development Flow
1. `git switch dev`
2. `git pull --ff-only origin dev`
3. `git switch -c feature/<short-name>`
4. Commit in small, reviewable chunks.
5. Open PR into `dev`.
6. Wait for CI checks and at least 1 approval.
7. Merge using squash or merge commit.

## Pull Request Requirements
1. PR template completed (scope, risk, test notes).
2. CI checks must pass:
   - frontend lint/build
   - backend build/test
3. At least one reviewer approval.
4. No secrets in code or logs.

## Commit Style
- Use imperative tense:
  - `Add market cache bypass for top opportunity`
  - `Fix deduplication threshold comparison`

## Testing Expectations
- Run locally before PR:
  - `dotnet build Trading_Signal_App.sln`
  - `dotnet test Trading_Signal_App.sln`
  - `cd signal-ui && npm ci && npm run lint && npm run build`

## Security Rules
1. Never commit `.env`, API keys, or hook URLs.
2. Store secrets only in GitHub/Vercel/Render secret stores.
3. Rotate leaked keys immediately.
