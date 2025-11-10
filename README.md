# Braess-aware BFF

A production-ready ASP.NET Core (.NET 8) back-end-for-front-end that plans fan-out calls and takes controlled detours to keep the p95 latency for the `/api/dashboard` endpoint within the SLO. The planner observes downstream services, applies hysteresis per node, and exposes rich metrics, tests, and load tooling.

## Contents

- `src/BraessAware.Bff` – ASP.NET Core BFF, planner library, and metrics wiring  
- `samples/Downstreams/*Service` – minimal APIs representing primary/alternate backends  
- `tests/BraessAware.Bff.Tests` – unit tests for the planner, hysteresis, and stats store  
- `tests/BraessAware.Bff.Integration` – Testcontainers-based integration suite validating mandatory nodes and latency improvements  
- `load/` – k6 load script for comparing planner-off vs planner-on  
- `deploy/` – Prometheus + Grafana deployment assets and dashboards  
- `.github/workflows` – CI and benchmark automation  

## Getting started

1. **Restore & build**
   ```bash
   dotnet restore BraessAware.Bff.sln
   dotnet build BraessAware.Bff.sln
   ```

2. **Run downstream samples** (in separate terminals). Each service reads `SERVICE_ROLE=primary|alternate` and delay settings from the environment.
   ```bash
   # Users
   dotnet run --project samples/Downstreams/UserService/UserService/UserService.csproj --urls http://localhost:5081
   SERVICE_ROLE=alternate dotnet run --project samples/Downstreams/UserService/UserService/UserService.csproj --urls http://localhost:6081

   # Accounts
   dotnet run --project samples/Downstreams/AccountsService/AccountsService/AccountsService.csproj --urls http://localhost:5082
   SERVICE_ROLE=alternate dotnet run --project samples/Downstreams/AccountsService/AccountsService/AccountsService.csproj --urls http://localhost:6082

   # Recommendations
   dotnet run --project samples/Downstreams/RecommendationsService/RecommendationsService/RecommendationsService.csproj --urls http://localhost:5083
   SERVICE_ROLE=alternate dotnet run --project samples/Downstreams/RecommendationsService/RecommendationsService/RecommendationsService.csproj --urls http://localhost:6083
   ```

3. **Run the BFF**
   ```bash
   BRAESS_ENABLED=true dotnet run --project src/BraessAware.Bff/BraessAware.Bff/BraessAware.Bff.csproj --urls http://localhost:5000
   ```

4. **Hit the dashboard**
   ```bash
   curl -i http://localhost:5000/api/dashboard
   ```
   Responses include the JSON payload and an `x-braess-degraded` header listing nodes currently detoured to their alternate.

---

## Background: Braess’s Paradox

Braess’s paradox describes a counterintuitive situation where adding a faster path to a network can **increase overall congestion and latency**, because individual nodes optimize for their own routes instead of the system’s total cost.

In distributed systems, the same effect occurs when all traffic is routed to the seemingly fastest downstream. As load concentrates, latency spikes and tail percentiles worsen.  

The **Braess-aware planner** counteracts this by deliberately routing a bounded portion of requests to alternate nodes with slightly higher base latency. By distributing pressure more evenly, the system improves **cluster-level p95 and p99 latency** and keeps the `/api/dashboard` endpoint within its SLO.

---

## Planner configuration

`appsettings.json` contains a `BraessPlanner` section describing each fan-out node (primary, alternate, mandatory flag, detour cap). Environment variable `BRAESS_ENABLED` toggles the planner globally, while individual routes can be enabled/disabled via configuration.

Policies can be tuned with `PlannerPolicyOptions` (latency thresholds, hysteresis duration, error-rate tolerance). During integration tests we tighten the thresholds to trigger detours after a handful of slow calls.

## Metrics & observability

- **Prometheus endpoint:** `/metrics` exposes counters and gauges including:
  - `braess_bff_plans_total`
  - `braess_bff_detours_applied_total{node}`
  - `braess_bff_endpoint_cost_current`
  - `braess_bff_endpoint_cost_target`
  - `braess_bff_node_latency` histogram (per node, primary vs alternate)
- **Tracing:** OpenTelemetry instrumentation covers ASP.NET Core and outgoing HTTP clients.
- **Grafana dashboard:** `deploy/dashboards/braess-dashboard.json` graphs planner cost and detour rates. Use `deploy/docker-compose.yml` to spin up the BFF, Prometheus, and Grafana stack.

## Tests

```bash
# Unit + integration tests
DOTNET_ROLL_FORWARD=LatestMajor dotnet test BraessAware.Bff.sln --logger "trx;LogFileName=tests/results/test-results.trx"
```

Integration tests run WireMock containers to emulate downstream primaries/alternates and verify two key behaviours:

1. Mandatory nodes propagate failures if both primary/alternate are unhealthy.  
2. Planner-enabled runs reduce observed p95 latency compared to planner-off.

Test results land in `tests/results/` for CI upload.

## Load testing

The `load/dashboard.js` k6 script compares planner-off vs planner-on. See `load/README.md` for exact commands. Generated CSVs can be plotted to confirm the p95 improvement.

## Deploying observability stack

```bash
cd deploy
docker compose up --build
```

Grafana (http://localhost:3000) ships with the dashboard pre-provisioned (admin/admin). Prometheus scrapes the BFF at `http://bff:8080/metrics`.

## SLO tuning tips

- Adjust `PlannerPolicyOptions.DegradedP95Threshold` to match downstream SLOs.  
- Increase `MaxDetourShare` cautiously (default 20%) to limit Braess-induced overload on alternates.  
- Use the `braess` JSON envelope + header to audit which nodes detour most frequently.

## Load comparison workflow

1. Run downstreams with exaggerated primary delay (e.g., `PRIMARY_DELAY_MS=450`).  
2. Execute the load script with planner off/on, capture CSV outputs.  
3. Plot the resulting p95 columns – you should observe a 30–40% improvement once the planner reroutes to alternates.

## License

MIT
