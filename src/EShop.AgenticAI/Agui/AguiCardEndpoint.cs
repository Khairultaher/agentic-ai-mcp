using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EShop.AgenticAI.Agui;

internal static class AguiCardEndpoint
{
    public static void MapAguiCard(this IEndpointRouteBuilder routes)
    {
        // POST /agui — AG-UI SSE stream. Body is the AG-UI RunAgentInput shape
        // (we only read `threadId` / `runId` / `messages`; the rest is ignored).
        routes.MapPost("/agui", async (HttpContext ctx, AguiRunInput? input, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var threadId  = input?.ThreadId  ?? Guid.NewGuid().ToString("N");
            var runId     = input?.RunId     ?? Guid.NewGuid().ToString("N");
            var messageId = Guid.NewGuid().ToString("N");
            var question  = input?.Messages?.LastOrDefault(m => m.Role == "user")?.Content
                            ?? "Show me a featured product.";

            // 1. RUN_STARTED
            await WriteAsync(ctx.Response, new RunStartedEvent(threadId, runId), ct);

            // 2. Streaming assistant text — the human-readable summary.
            await WriteAsync(ctx.Response, new TextMessageStartEvent(messageId), ct);
            foreach (var chunk in ChunkSummary(question))
            {
                await WriteAsync(ctx.Response, new TextMessageContentEvent(messageId, chunk), ct);
                await Task.Delay(40, ct);
            }
            await WriteAsync(ctx.Response, new TextMessageEndEvent(messageId), ct);

            // 3. CUSTOM event — the generative-UI payload the frontend renders.
            //    Name "html_card" is a project convention; frontends listen for it.
            var card = BuildSampleCard();
            var cardValue = JsonSerializer.SerializeToElement(new { html = card }, AguiSerializer.Options);
            await WriteAsync(ctx.Response, new CustomEvent("html_card", cardValue), ct);

            // 4. RUN_FINISHED
            await WriteAsync(ctx.Response, new RunFinishedEvent(threadId, runId), ct);
        });

        // GET /agui/demo — minimal client that consumes the SSE stream and renders the card.
        routes.MapGet("/agui/demo", () => Results.Content(DemoPage, "text/html; charset=utf-8"));
    }

    private static async Task WriteAsync(HttpResponse response, AguiEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize<AguiEvent>(evt, AguiSerializer.Options);
        var frame = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await response.Body.WriteAsync(frame, ct);
        await response.Body.FlushAsync(ct);
    }

    private static IEnumerable<string> ChunkSummary(string question)
    {
        var summary = $"Here is a featured product for: \"{question.Trim()}\".";
        const int size = 12;
        for (var i = 0; i < summary.Length; i += size)
        {
            yield return summary.Substring(i, Math.Min(size, summary.Length - i));
        }
    }

    private static string BuildSampleCard() => """
        <article style="font-family:system-ui,sans-serif;max-width:320px;border:1px solid #e2e8f0;border-radius:12px;overflow:hidden;box-shadow:0 4px 12px rgba(0,0,0,0.06);background:#fff;">
          <div style="background:linear-gradient(135deg,#6366f1,#8b5cf6);height:140px;display:flex;align-items:center;justify-content:center;color:#fff;font-size:48px;">🎧</div>
          <div style="padding:16px;">
            <h3 style="margin:0 0 4px;font-size:18px;color:#0f172a;">Aurora Wireless Headphones</h3>
            <p style="margin:0 0 12px;color:#64748b;font-size:13px;">Active noise cancelling · 30h battery</p>
            <div style="display:flex;align-items:center;justify-content:space-between;">
              <span style="font-size:22px;font-weight:600;color:#0f172a;">$249</span>
              <button style="background:#6366f1;color:#fff;border:0;padding:8px 14px;border-radius:8px;cursor:pointer;font-weight:500;">Add to cart</button>
            </div>
          </div>
        </article>
        """;

    private const string DemoPage = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <title>AG-UI HTML card demo</title>
          <style>
            body { font: 14px system-ui, sans-serif; max-width: 720px; margin: 32px auto; padding: 0 16px; color: #0f172a; }
            #log { white-space: pre-wrap; background: #f8fafc; border: 1px solid #e2e8f0; border-radius: 8px; padding: 12px; min-height: 64px; }
            #card { margin-top: 16px; }
            button { background: #0f172a; color: #fff; border: 0; padding: 8px 14px; border-radius: 8px; cursor: pointer; }
            input { width: 100%; padding: 8px; border: 1px solid #cbd5e1; border-radius: 8px; }
          </style>
        </head>
        <body>
          <h1>AG-UI HTML card demo</h1>
          <p>POSTs to <code>/agui</code>, parses the SSE stream, prints assistant text live, and renders the
             generative-UI <code>CUSTOM</code> event named <code>html_card</code>.</p>
          <input id="q" value="Show me a featured product." />
          <p><button id="go">Run agent</button></p>
          <h3>Assistant</h3>
          <div id="log"></div>
          <h3>Rendered card</h3>
          <div id="card"></div>

          <script>
            const log = document.getElementById('log');
            const card = document.getElementById('card');
            document.getElementById('go').onclick = run;

            async function run() {
              log.textContent = '';
              card.innerHTML = '';
              const body = {
                threadId: crypto.randomUUID(),
                runId: crypto.randomUUID(),
                messages: [{ id: crypto.randomUUID(), role: 'user', content: document.getElementById('q').value }],
              };
              const res = await fetch('/agui', {
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
                    if (!line.startsWith('data: ')) continue;
                    handle(JSON.parse(line.slice(6)));
                  }
                }
              }
            }

            function handle(evt) {
              switch (evt.type) {
                case 'TEXT_MESSAGE_CONTENT':
                  log.textContent += evt.delta;
                  break;
                case 'CUSTOM':
                  if (evt.name === 'html_card') card.innerHTML = evt.value.html;
                  break;
                // RUN_STARTED / TEXT_MESSAGE_START|END / RUN_FINISHED — no-op for this demo
              }
            }
          </script>
        </body>
        </html>
        """;
}

internal sealed record AguiRunInput(
    string? ThreadId,
    string? RunId,
    List<AguiMessage>? Messages);

internal sealed record AguiMessage(string? Id, string Role, string Content);
