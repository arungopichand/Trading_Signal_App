# Trading Signal App

Real-time stock intelligence platform with multi-source market data, confluence signal scoring, and live feed delivery.

## Stack
- Backend: ASP.NET Core (.NET 10), SignalR
- Frontend: React, Vite, Tailwind
- Market Data: Finnhub, Polygon, NewsAPI, Financial Modeling Prep
- Deployments: Render (backend), Vercel (frontend)
- CI/CD: GitHub Actions

## Repository Layout
```text
root/
├── docs/
├── src/
│   ├── backend/
│   ├── frontend/
│   └── shared/
├── tests/
│   ├── unit/
│   ├── integration/
│   └── e2e/
├── backend/            # active backend code
├── signal-ui/          # active frontend code
├── .github/workflows/
├── .env.example
├── README.md
└── CONTRIBUTING.md
```

## Quick Start (Local)
1. Copy `.env.example` to `.env` and fill keys.
2. Run backend:
   - `dotnet restore Trading_Signal_App.sln`
   - `dotnet run --project backend/SignalFeed.Api/SignalFeed.Api.csproj`
3. Run frontend:
   - `cd signal-ui`
   - `npm ci`
   - `npm run dev`

## Branching Model
- `main`: production
- `dev`: active integration branch
- `feature/<name>`: new features
- `bugfix/<name>`: non-urgent fixes
- `hotfix/<name>`: urgent production fixes

Flow: `feature/* -> dev -> main`

## CI/CD
- PR to `dev`: lint + build + test quality gate.
- Push/Merge to `dev`: staging deployment (Vercel preview + Render staging).
- Push/Merge to `main`: production deployment (Vercel production + Render production).

## Documentation
- [Project docs](docs/README.md)
- [Architecture](docs/architecture.md)
- [API](docs/api.md)
- [Deployment](docs/deployment.md)
