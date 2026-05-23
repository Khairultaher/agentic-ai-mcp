# Manual verification (Phase 1)

End-to-end smoke checks for the Ecom Analytics MCP stack: `ecom` (connection string to local SQL) + `dbinit` + `mcp` + `host`, plus the xUnit suite.

## Prerequisites

- **.NET 10 SDK** (matches `global.json`).
- **Local SQL Server `localhost`** reachable from your machine. The connecting Windows principal must have **CREATE DATABASE** permission — `dbinit` calls `MigrateAsync`, which provisions `e-shop-db` on first run. Connection string is wired in [src/EShop.AppHost/AppHost.cs](src/EShop.AppHost/AppHost.cs) (Windows auth by default; swap to `User Id=...;Password=...` for SQL auth).
- **Docker Desktop** running — required only for the `dotnet test` step (Testcontainers spins up a throwaway SQL Server). The AppHost no longer needs Docker.
- **Azure OpenAI** endpoint + key, supplied as environment variables in the shell that launches the AppHost (Aspire forwards them to the `host` child project):
  ```pwsh
  $env:AZURE_OPENAI_ENDPOINT = "https://<your>.openai.azure.com/"
  $env:AZURE_OPENAI_API_KEY = "<your-key>"
  $env:AZURE_OPENAI_DEPLOYMENT_NAME = "gpt-4o"   # optional; defaults to gpt-4o
  ```
  Endpoint and key are required — the `host` project throws on startup if either is missing. For a persistent setup, put the three vars under `environmentVariables` in `src/EShop.AppHost/Properties/launchSettings.json`.
- **`npx`** on PATH for the MCP Inspector.

## 1. Boot the stack

```pwsh
dotnet run --project src/EShop.AppHost
```

The Aspire dashboard opens. Resources to expect: `ecom` (connection-string resource — non-startable, just shows the value), `dbinit` (runs migrations + seed against `localhost.e-shop-db` and exits), `mcp`, `host`. Wait until `mcp` and `host` are green and `dbinit` has finished.

Spot-check the `dbinit` logs — you should see one of:

- `Seed complete: 8 zones, 6 categories, 48 products, 500 customers, ~3000 orders, ...` (first run — also creates the `e-shop-db` database if missing), or
- `Database already seeded; no rows inserted.` (subsequent runs — idempotency check).

If `dbinit` fails with a login or connection error, verify you can connect to `localhost` from `sqlcmd` or SSMS with the same Windows account that runs the AppHost.

## 2. MCP Inspector — list tools, resources, prompts

In the dashboard, open the `mcp` resource and copy its HTTPS endpoint (e.g. `https://localhost:xxxxx`).

```pwsh
npx @modelcontextprotocol/inspector
```

In the Inspector UI:

1. Transport: **Streamable HTTP** (SSE also works for legacy).
2. URL: paste the `mcp` endpoint base URL.
3. Connect.

Verify each tab:

| Tab       | Expect                                                                         |
|-----------|--------------------------------------------------------------------------------|
| Tools     | **5 tools** — `GetSalesSummary`, `GetOrdersByDateRange`, `GetStockLevels`, `GetUserGrowthTrend`, `GetProfitByZone`. Each shows a parameter schema with `[Description]` text. |
| Resources | **3 resources** — `schema://ecom` (static), `reports://low-stock` (static), `reports://daily-sales/{date}` (template).            |
| Prompts   | **3 prompts** — `analyze-sales-trend`, `stock-alert-summary`, `zone-performance-review`. Each renders argument placeholders.       |

## 3. Run each tool from the Inspector

Use sensible defaults (date params blank → defaults to last 30 days):

| Tool                   | Args                                              | Expected                                                                  |
|------------------------|---------------------------------------------------|---------------------------------------------------------------------------|
| `GetSalesSummary`      | `groupBy=Day`                                     | Non-empty array of `{ bucketStart, revenue>0, orderCount>0, averageOrderValue>0 }`.    |
| `GetOrdersByDateRange` | `page=1, pageSize=25`                             | `{ totalCount>0, items: [...] }` with rows containing `customerName`, `zoneName`, `region`, `status`. |
| `GetStockLevels`       | (no args)                                         | 48 product rows; some with non-null `daysOfCover`.                        |
| `GetUserGrowthTrend`   | `granularity=Week`                                | Weekly buckets with monotonically non-decreasing `cumulativeCustomers`.   |
| `GetProfitByZone`      | (no args)                                         | One row per active zone; `region ∈ {North, Central, South}`; `marginPercent` plausible. |

## 4. Read each resource from the Inspector

- `schema://ecom` — JSON with a `tables[]` array. Spot-check: `Orders` table lists columns `Id`, `CustomerId`, `ZoneId`, `OrderDate`, `Status`, `TotalAmount` with foreign keys to `Customers` and `Zones`.
- `reports://low-stock` — JSON `{ threshold: 300, generatedAt, items: [...] }` with `daysOfCover` ascending.
- `reports://daily-sales/{date}` — pick a date in the seeded window, e.g. yesterday. Should return non-zero `revenue` and a `topZones[]` array (5 entries on a busy day).

## 5. End-to-end with the Agentic Host

In the dashboard, open the `host` resource → click `/devui`. Chat with the `EShopAnalyst` agent. Three required questions (each answer must cite specific numbers + show a Markdown table, and the tool trace must name the right MCP tool):

1. "Show me sales by week for the last 90 days." → trace contains `GetSalesSummary` with `groupBy=Week`.
2. "Which products are below safety stock right now?" → trace contains `GetStockLevels` (and optionally a read of `reports://low-stock`).
3. "Compare gross margin across regions for the last 60 days." → trace contains `GetProfitByZone`.

### Five curl commands (raw HTTP API)

Run from the `host` resource endpoint (replace `$HOST` with the URL the dashboard surfaces):

```pwsh
# Sales trend
curl -X POST $HOST/ask -H "Content-Type: application/json" `
  -d '{"question":"Show me sales by week for the last 90 days."}'

# Stock alert
curl -X POST $HOST/ask -H "Content-Type: application/json" `
  -d '{"question":"Which products are below safety stock right now?"}'

# Zone margin
curl -X POST $HOST/ask -H "Content-Type: application/json" `
  -d '{"question":"Compare gross margin across regions for the last 60 days."}'

# User growth
curl -X POST $HOST/ask -H "Content-Type: application/json" `
  -d '{"question":"How fast are we acquiring new customers month over month?"}'

# Canonical prompt
curl -X POST $HOST/prompt/zone-performance-review -H "Content-Type: application/json" `
  -d '{"arguments":{"from":"","to":""}}'
```

Each response should be JSON `{ answer, trace[] }`. The `trace` array shows each `call` (with tool name + arguments) and `result` (with payload) the model fired during the chat loop.

## 6. Failure-mode check

Stop the `mcp` resource from the dashboard. Re-issue any `/ask` — the host should fail fast with a clear error mentioning the MCP transport, not a timeout.

## 7. Idempotency check

Stop the dashboard (`Ctrl+C`), then `dotnet run --project src/EShop.AppHost` again. `e-shop-db` on `localhost` persists across runs, so `dbinit` should log `Database already seeded; no rows inserted.` Row counts in `e-shop-db` stay stable.

To reset and re-seed from scratch, drop the database from SSMS / sqlcmd:

```sql
USE master;
ALTER DATABASE [e-shop-db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [e-shop-db];
```

Next AppHost run recreates and reseeds it.

## 8. Automated tests

```pwsh
dotnet test
```

The `EShop.McpServer.Tests` suite spins up a real SQL Server container per fixture, applies migrations, runs `EcomSeeder`, then asserts one fact per T5 tool against the seeded data. Requires Docker.

Expected: **5/5 passed** in `ToolTests`.
