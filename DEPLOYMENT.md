# Deployment Setup

## Live Services

- Frontend: `https://trading-signal-ui.vercel.app`
- Backend: `https://trading-signal-api-ozlf.onrender.com`
- Backend health: `https://trading-signal-api-ozlf.onrender.com/health`
- Backend signals: `https://trading-signal-api-ozlf.onrender.com/api/signals/current`

## Repository Layout

- `backend/SignalFeed.Api` contains the ASP.NET Core Web API.
- `signal-ui` contains the Vite React frontend.
- `render.yaml` provisions the Render backend service.
- `signal-ui/vercel.json` configures the Vercel frontend deployment.
- `.github/workflows/deploy.yml` runs CI/CD on every push to `main`.

## GitHub

1. Create a GitHub repository for this project.
2. Add this local repository as `origin`.
3. Push the `main` branch.
4. Add these repository secrets:
   - `VERCEL_TOKEN`

## Render

1. Render service is live at `trading-signal-api`.
2. The current backend URL is `https://trading-signal-api-ozlf.onrender.com`.
3. Render is connected to the `main` branch of this repository and auto-deploys on production pushes.
4. Render should have these backend environment variables:
   - `FINNHUB__APIKEY`
   - `SUPABASE_URL`
   - `SUPABASE_KEY`
   - `CORS_ALLOWED_ORIGINS`
5. Set `CORS_ALLOWED_ORIGINS` to `https://trading-signal-ui.vercel.app`.

## Vercel

1. Vercel project is live as `trading-signal-ui`.
2. The production frontend URL is `https://trading-signal-ui.vercel.app`.
3. GitHub Actions links the project by name using:
   - project: `trading-signal-ui`
   - scope: `arungopichands-projects`
4. Add the frontend environment variable:
   - `VITE_API_BASE_URL`
5. Set `VITE_API_BASE_URL=https://trading-signal-api-ozlf.onrender.com`

## Deployment Flow

1. Push to `dev` to run validation only.
2. Merge `dev` into `main` for production.
3. GitHub Actions builds the backend and frontend.
4. GitHub Actions deploys the frontend to Vercel from `main`.
5. Render auto-deploys the backend from `main`.
