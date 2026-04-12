# Test Strategy Scaffold

- `tests/unit/`: isolated logic/unit tests
- `tests/integration/`: service + API integration tests
- `tests/e2e/`: UI and end-to-end workflow tests
- `tests/reliability/`: soak/load/fault-injection assets

## Backend Reliability Test Suite

### Run .NET tests

```powershell
dotnet test backend/SignalFeed.Tests/SignalFeed.Tests.csproj -c Debug
```

### Soak test (30+ min)

```powershell
powershell -ExecutionPolicy Bypass -File tests/reliability/soak-runner.ps1 -BaseUrl http://localhost:10000 -DurationMinutes 30 -Concurrency 12
```

Output: `tests/reliability/soak-summary.json`

### Load test (k6)

```powershell
k6 run tests/reliability/load.k6.js
```

Optional:

```powershell
$env:BASE_URL="http://localhost:10000"; k6 run tests/reliability/load.k6.js
```
