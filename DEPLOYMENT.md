# Deployment Setup

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
   - `VERCEL_ORG_ID`
   - `VERCEL_PROJECT_ID`
   - `RENDER_DEPLOY_HOOK_URL`

## Render

1. In Render, create a new Blueprint service from this GitHub repository.
2. Render will read [`render.yaml`](/c:/Users/arung/Trading_Signal_App/render.yaml).
3. Set these backend environment variables in the Render dashboard:
   - `FINNHUB__APIKEY`
   - `SUPABASE_URL`
   - `SUPABASE_KEY`
   - `CORS_ALLOWED_ORIGINS`
4. Set `CORS_ALLOWED_ORIGINS` to your Vercel production URL, for example `https://your-app.vercel.app`.
5. Copy the backend deploy hook URL from Render and store it in GitHub as `RENDER_DEPLOY_HOOK_URL`.
6. Keep Render auto-deploy disabled for this service because GitHub Actions will trigger deploys after both builds pass.

## Vercel

1. Create a Vercel project using the `signal-ui` directory as the root.
2. Set the framework preset to Vite if Vercel does not detect it automatically.
3. Add the frontend environment variable:
   - `VITE_API_BASE_URL`
4. Set `VITE_API_BASE_URL` to your Render API origin, for example `https://trading-backend.onrender.com`.
5. Copy the Vercel token, org ID, and project ID into GitHub repository secrets.

## Deployment Flow

1. Push to `main`.
2. GitHub Actions builds the backend.
3. GitHub Actions builds the frontend.
4. GitHub Actions deploys the frontend to Vercel with the Vercel CLI.
5. GitHub Actions triggers the backend deploy hook on Render.
