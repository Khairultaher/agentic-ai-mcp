using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace EShop.AgenticAI.Agui;

// Interactive dashboard variant of the AG-UI demo.
// The LLM picks which panels are relevant to the user's question; the server then
// emits AG-UI CUSTOM events carrying (a) raw JSON rows and (b) a library-agnostic
// rendering directive. The frontend chooses a chart library (here: Chart.js) and
// renders each panel as it arrives.
internal static class AguiDashboardEndpoint
{
    public static void MapAguiDashboard(this IEndpointRouteBuilder routes)
    {
        // POST /agui/dashboard — AG-UI SSE stream.
        routes.MapPost("/agui/dashboard", async (
            HttpContext ctx,
            AguiRunInput? input,
            IChatClient chat,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var logger    = loggerFactory.CreateLogger("AguiDashboard");
            var threadId  = input?.ThreadId ?? Guid.NewGuid().ToString("N");
            var runId     = input?.RunId    ?? Guid.NewGuid().ToString("N");
            var messageId = Guid.NewGuid().ToString("N");
            var question  = input?.Messages?.LastOrDefault(m => m.Role == "user")?.Content
                            ?? "Show me today's e-commerce overview.";

            await WriteAsync(ctx.Response, new RunStartedEvent(threadId, runId), ct);

            // 1) Ask the model which panels are relevant + a one-line narrative.
            var plan = await PlanAsync(chat, question, logger, ct);
            var selected = plan.Panels
                .Select(id => PanelCatalog.All.FirstOrDefault(e => e.Id == id))
                .Where(e => e is not null)
                .Select(e => e!)
                .ToList();

            // 2) Narrative streamed as TEXT_MESSAGE_*.
            await WriteAsync(ctx.Response, new TextMessageStartEvent(messageId), ct);
            foreach (var chunk in ChunkText(plan.Narrative))
            {
                await WriteAsync(ctx.Response, new TextMessageContentEvent(messageId, chunk), ct);
                await Task.Delay(20, ct);
            }
            await WriteAsync(ctx.Response, new TextMessageEndEvent(messageId), ct);

            // 3) Layout skeleton — only the panels the planner picked.
            await EmitCustomAsync(ctx.Response, "dashboard.layout", new
            {
                gridCols = 12,
                panels = selected.Select(e => new { id = e.Id, title = e.Title, gridSpan = e.GridSpan }).ToArray(),
            }, ct);

            // 4) Each panel streams in as its data is ready. This is the seam for
            //    real MCP tool calls — replace each Build() with an MCP invocation.
            foreach (var entry in selected)
            {
                await Task.Delay(120, ct);
                await EmitCustomAsync(ctx.Response, "dashboard.panel", entry.Build(), ct);
            }

            await WriteAsync(ctx.Response, new RunFinishedEvent(threadId, runId), ct);
        });

        // GET /agui/dashboard/demo — minimal client (Chart.js via CDN) that consumes the SSE stream.
        routes.MapGet("/agui/dashboard/demo", () => Results.Content(DemoPage, "text/html; charset=utf-8"));
    }

    // ---- LLM planner -----------------------------------------------------------

    private static async Task<DashboardPlan> PlanAsync(
        IChatClient chat, string question, ILogger logger, CancellationToken ct)
    {
        var catalogText = string.Join("\n",
            PanelCatalog.All.Select(e => $"- {e.Id}: {e.Description}"));

        var system = $$"""
            You plan an e-commerce analytics dashboard. Given the user's question,
            pick the panels that best answer it from this catalog and write one
            short sentence describing what the dashboard shows.

            Catalog (id: description):
            {{catalogText}}

            Rules:
            - Only emit panel IDs that appear in the catalog above. No invented IDs.
            - Pick the minimal relevant set (typically 2–6). Avoid showing everything.
            - If the question is broad ("overview", "summary"), include the four kpi.*
              panels plus 2–3 charts.
            - If the question is about a single topic (stock, customers, zones, products,
              revenue), pick only the panels on that topic.

            Respond with STRICT JSON only — no markdown fences, no prose, no comments:
            {"panels":["<id>","<id>"],"narrative":"<one short sentence>"}
            """;

        try
        {
            var response = await chat.GetResponseAsync(
                new[]
                {
                    new ChatMessage(ChatRole.System, system),
                    new ChatMessage(ChatRole.User, question),
                },
                // Some Azure OpenAI deployments (reasoning models) only allow temperature=1,
                // so we don't set it. ResponseFormat.Json is widely supported and is what we need.
                new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
                ct);

            var text = StripFences(response.Text ?? "");
            var plan = JsonSerializer.Deserialize<DashboardPlan>(text, AguiSerializer.Options);
            if (plan?.Panels is { Length: > 0 })
            {
                var valid = plan.Panels
                    .Where(id => PanelCatalog.All.Any(e => e.Id == id))
                    .Distinct()
                    .ToArray();
                if (valid.Length > 0)
                {
                    return new DashboardPlan(valid, plan.Narrative ?? DefaultNarrative(question));
                }
            }
            logger.LogWarning("Planner returned no valid panels. Raw text: {Text}", text);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Dashboard planner failed; falling back to full panel set.");
        }

        // Fallback: show everything so the demo never breaks.
        return new DashboardPlan(
            PanelCatalog.All.Select(e => e.Id).ToArray(),
            DefaultNarrative(question));
    }

    private static string DefaultNarrative(string question) =>
        $"Showing the full overview for: \"{question.Trim()}\".";

    private static string StripFences(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```"))
        {
            var firstNl = s.IndexOf('\n');
            if (firstNl > 0) s = s[(firstNl + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
        }
        return s.Trim();
    }

    private sealed record DashboardPlan(string[] Panels, string Narrative);

    // ---- SSE helpers -----------------------------------------------------------

    private static async Task EmitCustomAsync(HttpResponse response, string name, object value, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToElement(value, AguiSerializer.Options);
        await WriteAsync(response, new CustomEvent(name, json), ct);
    }

    private static async Task WriteAsync(HttpResponse response, AguiEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize<AguiEvent>(evt, AguiSerializer.Options);
        var frame = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await response.Body.WriteAsync(frame, ct);
        await response.Body.FlushAsync(ct);
    }

    private static IEnumerable<string> ChunkText(string text)
    {
        const int size = 14;
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }

    // ---- Demo client (Chart.js via CDN) ----------------------------------------

    private const string DemoPage = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <title>AG-UI dashboard demo</title>
          <script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.0/dist/chart.umd.min.js"></script>
          <style>
            :root { --bg:#f8fafc; --card:#fff; --border:#e2e8f0; --muted:#64748b; --ink:#0f172a; --brand:#6366f1; }
            * { box-sizing: border-box; }
            body { font: 14px system-ui, sans-serif; background: var(--bg); color: var(--ink); margin: 0; padding: 24px; }
            header { max-width: 1200px; margin: 0 auto 16px; }
            h1 { margin: 0 0 4px; font-size: 20px; }
            p.sub { margin: 0; color: var(--muted); }
            #log { max-width:1200px; margin:8px auto 16px; white-space:pre-wrap; color:var(--muted); min-height:18px; }
            .toolbar { max-width:1200px; margin:0 auto 16px; display:flex; gap:8px; flex-wrap: wrap; }
            input { flex:1; min-width: 320px; padding:8px 12px; border:1px solid var(--border); border-radius:8px; background:var(--card); }
            button { background: var(--ink); color:#fff; border:0; padding:8px 14px; border-radius:8px; cursor:pointer; }
            .chips { max-width:1200px; margin:0 auto 16px; display:flex; gap:6px; flex-wrap:wrap; }
            .chip { background:#fff; border:1px solid var(--border); border-radius: 999px; padding:4px 10px; font-size:12px; color:var(--muted); cursor:pointer; }
            .chip:hover { color: var(--ink); border-color: var(--ink); }
            .grid { max-width: 1200px; margin: 0 auto; display: grid; grid-template-columns: repeat(12, 1fr); gap: 16px; }
            .panel { background: var(--card); border: 1px solid var(--border); border-radius: 12px; padding: 16px; box-shadow: 0 1px 2px rgba(0,0,0,0.03); min-height: 120px; display: flex; flex-direction: column; }
            .panel h3 { margin: 0 0 12px; font-size: 13px; font-weight: 600; color: var(--muted); text-transform: uppercase; letter-spacing: .04em; }
            .panel .body { flex: 1; position: relative; min-height: 80px; }
            .skeleton { background: linear-gradient(90deg,#f1f5f9 0%,#e2e8f0 50%,#f1f5f9 100%); background-size:200% 100%; animation: shine 1.4s infinite; border-radius:8px; height:100%; min-height:80px; }
            @keyframes shine { 0%{background-position:200% 0} 100%{background-position:-200% 0} }
            .stat .num { font-size: 32px; font-weight: 600; }
            .stat .trend { font-size: 12px; margin-top: 4px; }
            .stat .up { color: #059669; } .stat .down { color: #dc2626; }
            table { width:100%; border-collapse: collapse; font-size:13px; }
            th, td { padding: 6px 8px; border-bottom: 1px solid var(--border); text-align: left; }
            th { color: var(--muted); font-weight: 500; }
            td.right, th.right { text-align: right; font-variant-numeric: tabular-nums; }
            canvas { max-height: 260px; }
          </style>
        </head>
        <body>
          <header>
            <h1>AG-UI dashboard demo</h1>
            <p class="sub">The model picks the right panels for your question, then the server streams them as AG-UI <code>CUSTOM</code> events (raw data + chart directive). Try a focused question or a broad one.</p>
          </header>
          <div class="toolbar">
            <input id="q" value="How is stock holding up today?" />
            <button id="go">Run</button>
          </div>
          <div class="chips" id="examples"></div>
          <div id="log"></div>
          <div id="grid" class="grid"></div>

          <script>
            const grid = document.getElementById('grid');
            const log  = document.getElementById('log');
            const charts = new Map();
            document.getElementById('go').onclick = run;

            const examples = [
              "How is stock holding up today?",
              "Show me revenue and orders this week.",
              "Which zones are most profitable?",
              "Are we acquiring customers fast enough?",
              "Give me a full overview of the shop today.",
              "What are the top selling products right now?",
            ];
            const chips = document.getElementById('examples');
            for (const e of examples) {
              const c = document.createElement('span');
              c.className = 'chip'; c.textContent = e;
              c.onclick = () => { document.getElementById('q').value = e; run(); };
              chips.appendChild(c);
            }
            run();

            async function run() {
              log.textContent = '';
              grid.innerHTML = '';
              for (const c of charts.values()) c.destroy();
              charts.clear();

              const body = {
                threadId: crypto.randomUUID(),
                runId: crypto.randomUUID(),
                messages: [{ id: crypto.randomUUID(), role: 'user', content: document.getElementById('q').value }],
              };
              const res = await fetch('/agui/dashboard', {
                method: 'POST',
                headers: { 'content-type': 'application/json', 'accept': 'text/event-stream' },
                body: JSON.stringify(body),
              });

              const reader = res.body.getReader();
              const decoder = new TextDecoder();
              let buf = '';
              while (true) {
                const { value, done } = await reader.read();
                if (done) break;
                buf += decoder.decode(value, { stream: true });
                let idx;
                while ((idx = buf.indexOf('\n\n')) >= 0) {
                  const frame = buf.slice(0, idx);
                  buf = buf.slice(idx + 2);
                  for (const line of frame.split('\n')) {
                    if (line.startsWith('data: ')) handle(JSON.parse(line.slice(6)));
                  }
                }
              }
            }

            function handle(evt) {
              if (evt.type === 'TEXT_MESSAGE_CONTENT') { log.textContent += evt.delta; return; }
              if (evt.type !== 'CUSTOM') return;
              if (evt.name === 'dashboard.layout') return renderLayout(evt.value);
              if (evt.name === 'dashboard.panel')  return renderPanel(evt.value);
            }

            function renderLayout(layout) {
              for (const p of layout.panels) {
                const card = document.createElement('section');
                card.className = 'panel';
                card.id = `panel-${p.id}`;
                card.style.gridColumn = `span ${p.gridSpan || 4}`;
                card.innerHTML = `<h3>${escapeHtml(p.title)}</h3><div class="body"><div class="skeleton"></div></div>`;
                grid.appendChild(card);
              }
            }

            function renderPanel(panel) {
              const host = document.querySelector(`#panel-${CSS.escape(panel.id)} .body`);
              if (!host) return;
              host.innerHTML = '';
              const { kind } = panel.chart;
              if (kind === 'stat')     return renderStat(host, panel);
              if (kind === 'table')    return renderTable(host, panel);
              if (kind === 'line' || kind === 'bar' || kind === 'doughnut') return renderChartJs(host, panel);
              host.textContent = `Unsupported chart kind: ${kind}`;
            }

            function renderStat(host, panel) {
              const v = panel.data[0]?.value ?? 0;
              const opt = panel.chart.options || {};
              const num = opt.format === 'currency'
                ? new Intl.NumberFormat('en-US', { style: 'currency', currency: opt.currency || 'USD', maximumFractionDigits: 0 }).format(v)
                : new Intl.NumberFormat('en-US').format(v);
              const trend = opt.trend;
              const trendHtml = trend == null ? '' :
                `<div class="trend ${trend >= 0 ? 'up' : 'down'}">${trend >= 0 ? '▲' : '▼'} ${Math.abs(trend).toFixed(1)}% vs last week</div>`;
              host.classList.add('stat');
              host.innerHTML = `<div class="num">${num}</div>${trendHtml}`;
            }

            function renderTable(host, panel) {
              const cols = panel.chart.options?.columns ?? Object.keys(panel.data[0] ?? {}).map(f => ({ field: f, label: f }));
              const fmt = (v, c) => c.format === 'currency'
                ? new Intl.NumberFormat('en-US', { style:'currency', currency:'USD', maximumFractionDigits:0 }).format(v)
                : v;
              const head = cols.map(c => `<th class="${c.align === 'right' ? 'right' : ''}">${escapeHtml(c.label)}</th>`).join('');
              const rows = panel.data.map(r =>
                '<tr>' + cols.map(c => `<td class="${c.align === 'right' ? 'right' : ''}">${escapeHtml(String(fmt(r[c.field], c) ?? ''))}</td>`).join('') + '</tr>'
              ).join('');
              host.innerHTML = `<table><thead><tr>${head}</tr></thead><tbody>${rows}</tbody></table>`;
            }

            function renderChartJs(host, panel) {
              const canvas = document.createElement('canvas');
              host.appendChild(canvas);
              const c = new Chart(canvas, toChartJsConfig(panel));
              charts.set(panel.id, c);
            }

            function toChartJsConfig(panel) {
              const { kind, encoding = {}, options = {} } = panel.chart;
              const data = panel.data;

              if (kind === 'doughnut') {
                return {
                  type: 'doughnut',
                  data: {
                    labels: data.map(r => r[encoding.label.field]),
                    datasets: [{ data: data.map(r => r[encoding.value.field]),
                                 backgroundColor: ['#059669', '#f59e0b', '#dc2626', '#6366f1', '#0ea5e9'] }],
                  },
                  options: { responsive: true, maintainAspectRatio: false,
                             plugins: { legend: { position: 'bottom' } } },
                };
              }

              const xField = encoding.x?.field;
              const yField = encoding.y?.field;
              return {
                type: kind,
                data: {
                  labels: data.map(r => r[xField]),
                  datasets: [{
                    label: yField,
                    data: data.map(r => r[yField]),
                    borderColor: '#6366f1',
                    backgroundColor: options.fill ? 'rgba(99,102,241,0.15)' : '#6366f1',
                    fill: !!options.fill,
                    tension: options.smooth ? 0.35 : 0,
                  }],
                },
                options: {
                  responsive: true, maintainAspectRatio: false,
                  plugins: { legend: { display: false } },
                  scales: {
                    y: { title: { display: !!options.yLabel, text: options.yLabel } },
                    x: { grid: { display: false } },
                  },
                },
              };
            }

            function escapeHtml(s) { return String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
          </script>
        </body>
        </html>
        """;
}

// ---- Panel catalog -------------------------------------------------------------
// Each entry is what the planner picks from. Description goes verbatim into the
// system prompt so the LLM can match user intent to panel IDs.
internal static class PanelCatalog
{
    internal sealed record Entry(
        string Id,
        string Title,
        string Description,
        int GridSpan,
        Func<object> Build);

    public static readonly Entry[] All =
    {
        new("kpi.revenue",     "Revenue today",       "Single big-number KPI of today's gross revenue with WoW trend.", 3, BuildRevenueKpi),
        new("kpi.orders",      "Orders today",        "Single big-number KPI of today's order count with WoW trend.",   3, BuildOrdersKpi),
        new("kpi.aov",         "Avg order value",     "Single big-number KPI of today's average order value.",          3, BuildAovKpi),
        new("kpi.customers",   "New customers (7d)",  "Single big-number KPI of new customers acquired in last 7 days.",3, BuildCustomersKpi),
        new("revenue.trend",   "Revenue (last 14d)",  "Line chart of daily revenue over the last 14 days.",             8, BuildRevenueTrend),
        new("orders.by.day",   "Orders by day (7d)",  "Bar chart of order counts per day for the last week.",           6, BuildOrdersByDay),
        new("stock.status",    "Stock health",        "Doughnut chart of SKU counts by stock status (Healthy/Low/Out).",4, BuildStockStatus),
        new("zone.profit",     "Profit by zone",      "Bar chart of profit grouped by geographic zone.",                6, BuildZoneProfit),
        new("customer.growth", "Customer growth (4w)","Line chart of new-customer signups per week for the last 4 weeks.",6, BuildCustomerGrowth),
        new("top.products",    "Top products today",  "Table of top-selling products with units sold and revenue.",     6, BuildTopProducts),
    };

    // Each builder creates its own Random so panels stay independent.

    private static object BuildRevenueKpi()
    {
        var rng = new Random();
        return Panel("kpi.revenue",
            Chart("stat", options: new { format = "currency", currency = "USD", trend = +6.4 }),
            new object[] { new { value = 18_420 + rng.Next(-2_000, 2_000) } });
    }

    private static object BuildOrdersKpi()
    {
        var rng = new Random();
        return Panel("kpi.orders",
            Chart("stat", options: new { format = "number", trend = +2.1 }),
            new object[] { new { value = 312 + rng.Next(-40, 40) } });
    }

    private static object BuildAovKpi()
    {
        var rng = new Random();
        var rev = 18_420 + rng.Next(-2_000, 2_000);
        var ord = 312 + rng.Next(-40, 40);
        return Panel("kpi.aov",
            Chart("stat", options: new { format = "currency", currency = "USD", trend = +4.3 }),
            new object[] { new { value = Math.Round(rev / (double)ord, 2) } });
    }

    private static object BuildCustomersKpi()
    {
        var rng = new Random();
        return Panel("kpi.customers",
            Chart("stat", options: new { format = "number", trend = -1.2 }),
            new object[] { new { value = 87 + rng.Next(-15, 15) } });
    }

    private static object BuildRevenueTrend()
    {
        var rng = new Random();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = Enumerable.Range(0, 14)
            .Select(i => new
            {
                date = today.AddDays(i - 13).ToString("yyyy-MM-dd"),
                revenue = 12_000 + rng.Next(0, 8_000) + (i * 220),
            })
            .Cast<object>()
            .ToArray();
        return Panel("revenue.trend",
            Chart("line",
                encoding: new { x = new { field = "date", type = "temporal" },
                                y = new { field = "revenue", type = "quantitative" } },
                options: new { yLabel = "USD", smooth = true, fill = true }),
            rows);
    }

    private static object BuildOrdersByDay()
    {
        var rng = new Random();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = Enumerable.Range(0, 7)
            .Select(i => new
            {
                day   = today.AddDays(i - 6).ToString("ddd"),
                count = 240 + rng.Next(0, 180),
            })
            .Cast<object>()
            .ToArray();
        return Panel("orders.by.day",
            Chart("bar",
                encoding: new { x = new { field = "day",   type = "categorical" },
                                y = new { field = "count", type = "quantitative" } },
                options: new { yLabel = "Orders" }),
            rows);
    }

    private static object BuildStockStatus() =>
        Panel("stock.status",
            Chart("doughnut",
                encoding: new { label = new { field = "status" },
                                value = new { field = "skuCount" } }),
            new object[]
            {
                new { status = "Healthy", skuCount = 1842 },
                new { status = "Low",     skuCount = 137  },
                new { status = "Out",     skuCount = 21   },
            });

    private static object BuildZoneProfit()
    {
        var rng = new Random();
        var rows = new[] { "North", "South", "East", "West", "Central" }
            .Select(z => new { zone = z, profit = 4_500 + rng.Next(0, 9_000) })
            .Cast<object>().ToArray();
        return Panel("zone.profit",
            Chart("bar",
                encoding: new { x = new { field = "zone",   type = "categorical" },
                                y = new { field = "profit", type = "quantitative" } },
                options: new { yLabel = "USD" }),
            rows);
    }

    private static object BuildCustomerGrowth()
    {
        var rng = new Random();
        var rows = Enumerable.Range(0, 4)
            .Select(i => new
            {
                week  = $"W{i + 1}",
                count = 60 + rng.Next(0, 50) + (i * 8),
            })
            .Cast<object>()
            .ToArray();
        return Panel("customer.growth",
            Chart("line",
                encoding: new { x = new { field = "week",  type = "categorical" },
                                y = new { field = "count", type = "quantitative" } },
                options: new { yLabel = "Customers", smooth = true }),
            rows);
    }

    private static object BuildTopProducts() =>
        Panel("top.products",
            Chart("table",
                options: new { columns = new[]
                {
                    new { field = "sku",     label = "SKU",     align = "left",  format = (string?)null },
                    new { field = "name",    label = "Product", align = "left",  format = (string?)null },
                    new { field = "units",   label = "Units",   align = "right", format = (string?)null },
                    new { field = "revenue", label = "Revenue", align = "right", format = (string?)"currency" },
                }}),
            new object[]
            {
                new { sku = "AUR-001", name = "Aurora Wireless Headphones", units = 142, revenue = 35_358 },
                new { sku = "LUM-220", name = "Lumen Smart Lamp",           units = 118, revenue = 14_042 },
                new { sku = "TER-330", name = "Terra Hiking Backpack",      units =  96, revenue = 11_424 },
                new { sku = "NOV-040", name = "Nova Mechanical Keyboard",   units =  73, revenue = 13_797 },
                new { sku = "ZEP-150", name = "Zephyr Travel Mug",          units =  68, revenue =  2_312 },
            });

    private static object Panel(string id, object chart, object[] data) =>
        new { id, chart, data };

    private static object Chart(string kind, object? encoding = null, object? options = null) =>
        new { kind, encoding, options };
}
