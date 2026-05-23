# Plan: Ecom Analytics MCP Server + Agentic AI Host on .NET 10 / Aspire

## Context

We want to learn and demonstrate the full Model Context Protocol (MCP) triad — **MCP Server, MCP Client, MCP Host** — using **.NET 10** and **Aspire 10** as the orchestration shell. The business surface is an **ecommerce analytics** workload: orders, sales, stock, user growth, and zone/region profit. Phase 1 ships the core five analytics; the design must let us extend to cohort/retention, returns, marketing attribution, etc., without re-plumbing.

The Aspire dashboard (with `MapDevUI`) is the operator surface — it visualises every resource (SQL Server, MCP Server, Agentic Host, model endpoint) and gives us a built-in playground to call the Host endpoints, so we ship **zero custom front-end** in v1.

**Stack choices (locked):** SQL Server container · MCP over HTTP+SSE · Agentic Host calls Azure OpenAI via `Microsoft.Extensions.AI` · client surface is Aspire `MapDevUI` · scope is "core five" analytics, extensible.

## High-level architecture

```
+--------------------+      +---------------------+      +----------------------+
|  Aspire AppHost    |----->|  SQL Server (cont.) |<-----|  EShop.Data (EF Core)|
|  + DevUI dashboard |      |  db: ecom           |      |  DbContext + seed    |
+----------+---------+      +---------------------+      +----------+-----------+
           |                                                         ^
           v                                                         |
+----------+---------+      HTTP+SSE      +----------------------+   |
|  EShop.AgenticHost |<------------------>|  EShop.McpServer     |---+
|  (ASP.NET Core API)|  MCP client SDK    |  Tools/Resources/    |
|  Azure OpenAI +    |                    |  Prompts             |
|  MapDevUI playground|                   +----------------------+
+--------------------+
```

Solution layout:

```
/agentic_ai_mcp.sln
  /src
    EShop.AppHost/          (Aspire orchestration; .NET 10)
    EShop.ServiceDefaults/  (telemetry, health, resilience defaults)
    EShop.Data/             (EF Core entities, DbContext, seeder)
    EShop.McpServer/        (ASP.NET Core, ModelContextProtocol HTTP+SSE)
    EShop.AgenticHost/      (ASP.NET Core API, MCP client, AzureOpenAI, MapDevUI)
  /tests
    EShop.McpServer.Tests/  (xUnit)
```

## Domain model (ecom database)

| Table          | Key fields                                                                 |
|----------------|---------------------------------------------------------------------------|
| Zones          | Id, Name, Region                                                          |
| Customers      | Id, Name, Email, ZoneId, RegisteredOn                                     |
| Categories     | Id, Name                                                                  |
| Products       | Id, Name, CategoryId, Price, Cost, StockQty                               |
| Orders         | Id, CustomerId, ZoneId, OrderDate, Status, TotalAmount                    |
| OrderItems     | Id, OrderId, ProductId, Quantity, UnitPrice, UnitCost                     |
| StockMovements | Id, ProductId, MovementType (In/Out/Adjust), Quantity, MovedOn            |

Seed targets: 8 zones across 3 regions, ~50 products in 6 categories, ~500 customers, ~3,000 orders across the last 12 months with a realistic weekly seasonality so trends are visible.

---

## Tasks

### T1 — Solution + Aspire AppHost scaffold

- **Goal:** Create the .NET 10 solution with Aspire AppHost and ServiceDefaults projects.
- **Why:** Every later task is hosted/observed by Aspire; we need the shell first.
- **Depends on:** —
- **Files to read first:**
  - https://learn.microsoft.com/dotnet/aspire/fundamentals/setup-tooling (Aspire 10 templates)
  - https://learn.microsoft.com/dotnet/aspire/fundamentals/app-host-overview
- **Deliverables:**
  - `agentic_ai_mcp.sln` at repo root
  - `src/EShop.AppHost/EShop.AppHost.csproj` (`<IsAspireHost>true`)
  - `src/EShop.ServiceDefaults/EShop.ServiceDefaults.csproj`
  - `global.json` pinning `dotnet 10.0.x`
- **Out of scope:** Any business resources (DB, MCP, Host) — just the shell.
- **Acceptance:**
  - `dotnet run --project src/EShop.AppHost` opens the Aspire dashboard with zero resources and no errors.
  - `dotnet build` is clean on a fresh clone.
- **Prompt block:**
  ```
  Scaffold a .NET 10 Aspire solution at the current directory. Create:
  - global.json pinning the 10.0 SDK
  - agentic_ai_mcp.sln
  - src/EShop.AppHost (Aspire 10 AppHost template)
  - src/EShop.ServiceDefaults (Aspire ServiceDefaults template)
  Wire ServiceDefaults into AppHost. Do not add any other resources yet.
  Verify: `dotnet build` succeeds and `dotnet run --project src/EShop.AppHost`
  brings up the dashboard.
  ```

### T2 — SQL Server resource + EShop.Data project

- **Goal:** Stand up a SQL Server container in Aspire and a `EShop.Data` library with EF Core `EcomDbContext` and entities.
- **Why:** All analytics derive from this database; we want one canonical schema and connection string flowing through Aspire.
- **Depends on:** T1
- **Files to read first:**
  - https://learn.microsoft.com/dotnet/aspire/database/sql-server-integration
  - `src/EShop.AppHost/AppHost.cs` (from T1)
- **Deliverables:**
  - `Aspire.Hosting.SqlServer` package in AppHost; `AddSqlServer("sql").AddDatabase("ecom")` with a persistent volume.
  - `src/EShop.Data/` with: `Zone`, `Customer`, `Category`, `Product`, `Order`, `OrderItem`, `StockMovement` entities; `EcomDbContext`; one initial EF migration.
  - Connection string consumed by referencing projects via `builder.AddSqlServerDbContext<EcomDbContext>("ecom")`.
- **Out of scope:** Seed data (T3), tools (T5).
- **Acceptance:**
  - Aspire dashboard shows `sql` resource healthy with `ecom` DB.
  - A throwaway `dotnet ef migrations add Init` produces a migration that compiles.
  - Connecting with SSMS / `sqlcmd` shows the seven tables.
- **Prompt block:**
  ```
  Add SQL Server to the Aspire AppHost: `AddSqlServer("sql").WithDataVolume()
  .AddDatabase("ecom")`. Create src/EShop.Data with EF Core entities for
  Zone, Customer, Category, Product, Order, OrderItem, StockMovement (see
  the domain table in the plan). Configure EcomDbContext, decimal precision
  for Price/Cost/TotalAmount, and the required FKs. Add an initial migration.
  Reference EShop.Data from AppHost so the connection string flows through.
  Do NOT seed data in this task.
  ```

### T3 — Seed data with realistic distributions

- **Goal:** Populate `ecom` with deterministic, realistic data so analytics produce meaningful charts.
- **Why:** Flat random data hides trend behaviour; we need seasonality + zone variance to make tools visibly useful.
- **Depends on:** T2
- **Files to read first:**
  - `src/EShop.Data/EcomDbContext.cs`
  - `src/EShop.Data/Entities/*.cs`
- **Deliverables:**
  - `src/EShop.Data/Seeding/EcomSeeder.cs` — idempotent, uses a fixed `Random(seed)`.
  - Aspire `WaitFor` hook so seeding runs after migrations on AppHost start.
  - Volumes set so re-runs don't double-seed (check for existing rows).
- **Out of scope:** Multi-tenant / multi-currency data.
- **Acceptance:**
  - Running the AppHost twice keeps row counts stable.
  - Orders span 12 months with weekly seasonality (weekend bump).
  - At least 3 zones in different regions show differing profit margins.
- **Prompt block:**
  ```
  Implement EShop.Data/Seeding/EcomSeeder.cs as an idempotent seeder using
  a fixed RNG seed. Generate: 8 zones across 3 regions; 6 categories;
  ~50 products with realistic price/cost spread; ~500 customers spread
  across zones with RegisteredOn over the last 18 months; ~3,000 orders
  over the last 12 months with weekly seasonality (Fri-Sun bump) and a
  small per-zone margin bias; OrderItems and matching StockMovements.
  Call it from EShop.AppHost startup after migrations. Make re-runs no-ops.
  ```

### T4 — EShop.McpServer (HTTP+SSE) project skeleton

- **Goal:** Stand up an ASP.NET Core MCP server using the official `ModelContextProtocol` C# SDK over HTTP+SSE, registered as an Aspire project resource.
- **Why:** This is the protocol surface; tools, resources, prompts (T5–T7) bolt onto it.
- **Depends on:** T2 (needs `EcomDbContext`)
- **Files to read first:**
  - https://github.com/modelcontextprotocol/csharp-sdk (README + samples)
  - https://modelcontextprotocol.io/specification (Tools / Resources / Prompts contracts)
- **Deliverables:**
  - `src/EShop.McpServer/EShop.McpServer.csproj` referencing `ModelContextProtocol`, `ModelContextProtocol.AspNetCore`, `EShop.Data`, `EShop.ServiceDefaults`.
  - `Program.cs` registers `AddMcpServer().WithHttpTransport()` and `app.MapMcp()`.
  - Registered in AppHost as `builder.AddProject<EShop_McpServer>("mcp").WithReference(ecomDb).WaitFor(ecomDb)`.
- **Out of scope:** Any tool/resource/prompt implementations.
- **Acceptance:**
  - `GET /sse` returns an open SSE stream.
  - MCP Inspector (`npx @modelcontextprotocol/inspector`) can connect to the SSE URL and list zero tools.
- **Prompt block:**
  ```
  Create src/EShop.McpServer (ASP.NET Core, .NET 10). Add NuGet refs:
  ModelContextProtocol, ModelContextProtocol.AspNetCore. Reference
  EShop.Data and EShop.ServiceDefaults. In Program.cs:
  builder.AddServiceDefaults();
  builder.Services.AddMcpServer().WithHttpTransport();
  builder.AddSqlServerDbContext<EcomDbContext>("ecom");
  app.MapMcp();
  Register in EShop.AppHost as project resource "mcp" with reference to
  the ecom database and WaitFor it. Verify with MCP Inspector against
  the SSE URL surfaced in the Aspire dashboard.
  ```

### T5 — Core five analytics tools

- **Goal:** Implement the five MCP **tools** that cover the v1 analytics surface.
- **Why:** Tools are the action verbs the LLM will call; they must return shaped, summarisable JSON.
- **Depends on:** T3, T4
- **Files to read first:**
  - `src/EShop.McpServer/Program.cs`
  - `src/EShop.Data/EcomDbContext.cs`
- **Deliverables:** A `Tools/` folder with `[McpServerToolType]` classes:
  - `GetSalesSummary(from, to, groupBy: day|week|month)` → revenue, orders, AOV.
  - `GetOrdersByDateRange(from, to, status?)` → paged orders.
  - `GetStockLevels(belowThreshold?)` → product + on-hand + days-of-cover.
  - `GetUserGrowthTrend(from, to, granularity)` → new customers per bucket.
  - `GetProfitByZone(from, to)` → zone, region, revenue, cost, gross profit, margin %.
  - Each tool has `[Description]` on parameters and returns DTOs with `[JsonPropertyName]`.
- **Out of scope:** Cohort, retention, returns, marketing attribution (future).
- **Acceptance:**
  - All five tools listed by MCP Inspector with parameter schemas.
  - Each tool returns non-empty results against seeded data for sensible inputs.
  - Tools never `SELECT *` — projection DTOs only; queries are `AsNoTracking()`.
- **Prompt block:**
  ```
  Add Tools/ to EShop.McpServer with five [McpServerToolType] classes
  implementing GetSalesSummary, GetOrdersByDateRange, GetStockLevels,
  GetUserGrowthTrend, GetProfitByZone (signatures in the plan). Each
  method is async, takes EcomDbContext via DI, uses AsNoTracking() and
  projects to a DTO record. Add [Description] on every parameter and
  return type. Validate date ranges (from <= to, default to last 30d).
  Register the tool types: AddMcpServer().WithToolsFromAssembly().
  Verify all five list in MCP Inspector and return data.
  ```

### T6 — MCP resources

- **Goal:** Expose read-only **resources** the LLM can pull as context without invoking a tool.
- **Why:** Schema awareness and pre-baked daily snapshots cut tool round-trips and improve answer quality.
- **Depends on:** T4
- **Files to read first:**
  - `src/EShop.McpServer/Program.cs`
  - https://modelcontextprotocol.io/specification — Resources section
- **Deliverables:**
  - `schema://ecom` → JSON describing tables, columns, FKs (generated from `EcomDbContext.Model`).
  - `reports://daily-sales/{yyyy-MM-dd}` → cached daily sales summary.
  - `reports://low-stock` → snapshot of products under threshold.
  - `AddMcpServer().WithResourcesFromAssembly()` registration.
- **Out of scope:** Writeable / subscribed resources.
- **Acceptance:**
  - MCP Inspector lists the three resources.
  - `schema://ecom` content matches actual EF model (spot-check 2 tables).
- **Prompt block:**
  ```
  Add Resources/ to EShop.McpServer with three [McpServerResourceType]
  classes: SchemaResource (URI schema://ecom, generated from
  EcomDbContext.Model.GetEntityTypes()), DailySalesResource
  (reports://daily-sales/{date}), LowStockResource (reports://low-stock).
  Register via AddMcpServer().WithResourcesFromAssembly(). Cache the
  schema JSON in a singleton on startup. Verify via MCP Inspector.
  ```

### T7 — MCP prompts

- **Goal:** Ship three analyst-workflow **prompt templates**.
- **Why:** Prompts let clients (and the Agentic Host) pick a canonical question shape; reduces prompt-engineering drift across users.
- **Depends on:** T5, T6
- **Files to read first:**
  - https://modelcontextprotocol.io/specification — Prompts section
  - `src/EShop.McpServer/Tools/*.cs`
- **Deliverables:**
  - `analyze-sales-trend(from, to, granularity)` — instructs the LLM to call `GetSalesSummary` then narrate trend.
  - `stock-alert-summary(threshold)` — uses `reports://low-stock` + `GetStockLevels`.
  - `zone-performance-review(from, to)` — uses `GetProfitByZone` and compares top vs bottom regions.
  - `AddMcpServer().WithPromptsFromAssembly()`.
- **Out of scope:** Prompt argument auto-completion suggestions.
- **Acceptance:**
  - MCP Inspector lists the three prompts; expanding each renders argument placeholders.
- **Prompt block:**
  ```
  Add Prompts/ to EShop.McpServer with three [McpServerPromptType]
  classes (analyze-sales-trend, stock-alert-summary, zone-performance-
  review). Each returns a List<ChatMessage> with a system instruction
  and a user message that names the exact MCP tools/resources to consult
  and the shape of the expected answer (summary + table). Register via
  WithPromptsFromAssembly().
  ```

### T8 — EShop.AgenticHost (MCP client + Azure OpenAI + MapDevUI)

- **Goal:** ASP.NET Core API that connects to the MCP server as a **client**, exposes Azure-OpenAI-backed chat endpoints, and surfaces them in Aspire `MapDevUI` for interactive testing.
- **Why:** This is the "Host" role of the MCP triad — it owns the model, mediates tool-use, and shows the end-to-end loop in the dashboard.
- **Depends on:** T5, T6, T7
- **Files to read first:**
  - https://learn.microsoft.com/dotnet/aspire/azureai/azure-openai-integration
  - https://learn.microsoft.com/dotnet/aspire/fundamentals/dev-ui (MapDevUI)
  - `src/EShop.McpServer/Program.cs` (for the MCP endpoint name)
- **Deliverables:**
  - `src/EShop.AgenticHost/` referencing `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, `Azure.AI.OpenAI`, `ModelContextProtocol` (client side), `EShop.ServiceDefaults`.
  - Aspire `AddAzureOpenAI("openai").AddDeployment("gpt-4o", "gpt-4o", "...")` (use existing deployment via connection string in dev).
  - Startup wires an `McpClient` against the `mcp` resource URL and registers `AsAIFunctions()` onto `IChatClient`.
  - Endpoints:
    - `POST /ask` `{ question }` → runs tool-using chat loop, returns answer + trace.
    - `POST /prompt/{name}` `{ args }` → resolves an MCP prompt and runs it.
  - `app.MapDevUI()` enabled in Development.
  - Registered in AppHost: `AddProject<EShop_AgenticHost>("host").WithReference(mcp).WithReference(openai).WaitFor(mcp)`.
- **Out of scope:** Auth, multi-turn session memory, streaming responses to the browser.
- **Acceptance:**
  - From the Aspire dashboard, opening the Host's DevUI lets you POST to `/ask` with "What were last month's sales by zone?" and see an answer plus a trace that includes a `GetProfitByZone` tool call.
  - Killing the MCP server makes `/ask` fail fast with a clear 503.
- **Prompt block:**
  ```
  Create src/EShop.AgenticHost (ASP.NET Core, .NET 10). Add packages:
  Microsoft.Extensions.AI, Microsoft.Extensions.AI.OpenAI, Azure.AI.OpenAI,
  ModelContextProtocol (client). In Program.cs:
   - builder.AddServiceDefaults();
   - builder.AddAzureOpenAIClient("openai");
   - Build an IMcpClient against the "mcp" service URL from Aspire config
     and register its tools as AIFunctions on a ChatClientBuilder.
   - Add endpoints POST /ask and POST /prompt/{name} as in the plan.
   - if (app.Environment.IsDevelopment()) app.MapDevUI();
  Register in EShop.AppHost with references to "mcp" and the AzureOpenAI
  resource. Verify end-to-end via the DevUI page in the dashboard.
  ```

### T9 — Tests + verification harness

- **Goal:** A thin xUnit project that exercises every MCP tool against an in-memory or testcontainer SQL Server, plus a manual verification script.
- **Why:** Locks in tool contracts before we extend beyond core five.
- **Depends on:** T5–T8
- **Files to read first:**
  - `src/EShop.McpServer/Tools/*.cs`
  - `src/EShop.Data/Seeding/EcomSeeder.cs`
- **Deliverables:**
  - `tests/EShop.McpServer.Tests/` with one test per tool asserting shape + non-empty results on seeded data.
  - `docs/verify.md` (or root `VERIFY.md`) with the five curl commands and three DevUI questions to run by hand.
- **Out of scope:** Load testing, fuzzing.
- **Acceptance:**
  - `dotnet test` is green.
  - Manual verification list in `VERIFY.md` succeeds end-to-end against a fresh `dotnet run --project src/EShop.AppHost`.
- **Prompt block:**
  ```
  Add tests/EShop.McpServer.Tests (xUnit). Use Testcontainers.MsSql to
  spin up a real SQL Server per test fixture; apply migrations and run
  EcomSeeder. Write one fact per tool from T5 asserting expected DTO
  shape and non-empty results. Add VERIFY.md at repo root with the
  manual smoke steps.
  ```

---

## Cross-cutting conventions

- **Async + AsNoTracking everywhere** in read paths; never `.ToList()` synchronously.
- **DTO projection** at the SQL boundary; never serialise EF entities directly.
- **`[Description]` on every MCP tool, parameter, and resource** — this is what the LLM sees.
- **No secrets in code** — Azure OpenAI key flows in via Aspire connection string / user-secrets in dev.
- **`Microsoft.Extensions.AI`** is the single chat abstraction; do not call `OpenAIClient` directly from endpoints.

## End-to-end verification (Phase 1 done when…)

1. `dotnet run --project src/EShop.AppHost` brings up the dashboard with `sql`, `ecom`, `mcp`, `host`, `openai` all healthy.
2. Open the `mcp` resource → SSE URL → connect MCP Inspector → see **5 tools, 3 resources, 3 prompts**.
3. Open the `host` resource → DevUI page → POST `/ask` with each of:
   - "Show me sales by week for the last 90 days."
   - "Which products are below safety stock right now?"
   - "Compare gross margin across regions for Q1."
   - "How fast are we acquiring new customers month over month?"
   - "Run the zone-performance-review prompt for the last 60 days."
4. Each answer's tool-call trace names the correct MCP tool/prompt and produces a coherent narrative + table.
5. `dotnet test` is green.

## Out of scope for this plan (parked for Phase 2)

- Cohort / retention / repeat-purchase analytics
- Returns and refunds
- Marketing attribution + campaign ROI
- Authn/Authz on the Host API
- Multi-turn session memory
- Streaming SSE responses to a browser UI
- Production deployment (AKS/ACA) and CI/CD
