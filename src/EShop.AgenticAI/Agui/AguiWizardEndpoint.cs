using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EShop.AgenticAI.Agui;

// Multi-step wizard variant. Demonstrates the bidirectional AG-UI pattern:
//   1. Server emits a form schema (CUSTOM "form.request") + STATE_SNAPSHOT, then RUN_FINISHED.
//   2. Client renders the form, user submits.
//   3. Client POSTs back on the SAME endpoint, carrying the (now-mutated) state and the
//      submitted values. Server validates, advances step, emits the next form. Loop until
//      the agent emits CUSTOM "form.complete" with the final structured payload.
//
// State flows on the wire — the server stays stateless across requests.
internal static class AguiWizardEndpoint
{
    public static void MapAguiWizard(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/agui/wizard", async (HttpContext ctx, WizardRunInput? input, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var threadId = input?.ThreadId ?? Guid.NewGuid().ToString("N");
            var runId    = input?.RunId    ?? Guid.NewGuid().ToString("N");

            // State: { step: int, answers: { [fieldName]: value } }
            var state = input?.State ?? new WizardState(Step: 0, Answers: new Dictionary<string, JsonElement>());
            var submission = input?.Submission;

            await WriteAsync(ctx.Response, new RunStartedEvent(threadId, runId), ct);

            // If the client just submitted step N, validate and either bounce back with errors
            // or merge the answers and advance to step N+1.
            Dictionary<string, string>? errors = null;
            if (submission is { } sub && state.Step >= 1 && state.Step <= Steps.Count)
            {
                var currentStep = Steps[state.Step - 1];
                (errors, var cleaned) = ValidateAndCoerce(currentStep, sub.Values);
                if (errors is null)
                {
                    var merged = new Dictionary<string, JsonElement>(state.Answers);
                    foreach (var (k, v) in cleaned!) merged[k] = v;
                    state = state with { Step = state.Step + 1, Answers = merged };
                }
                // else: keep state.Step pointing at the same form, re-emit it with errors below.
            }
            else if (submission is null && state.Step == 0)
            {
                // First call — bump into step 1.
                state = state with { Step = 1 };
            }

            // Terminal state — emit the completion event with the assembled order.
            if (state.Step > Steps.Count)
            {
                await EmitTextAsync(ctx.Response, "All set. Here is the order summary.", ct);
                await EmitCustomAsync(ctx.Response, "form.complete", new
                {
                    summary = BuildOrderSummary(state.Answers),
                    answers = state.Answers,
                }, ct);
            }
            else
            {
                // Emit the form for the current step (with errors echoed back if any).
                var step = Steps[state.Step - 1];
                await EmitTextAsync(ctx.Response,
                    errors is null
                        ? $"Step {state.Step} of {Steps.Count}: {step.Title}."
                        : $"Please correct the errors below.",
                    ct);

                await EmitCustomAsync(ctx.Response, "form.request", new
                {
                    formId      = step.Id,
                    step        = state.Step,
                    totalSteps  = Steps.Count,
                    title       = step.Title,
                    description = step.Description,
                    submitLabel = state.Step == Steps.Count ? "Place order" : "Continue",
                    fields      = step.Fields,
                    // Pre-fill any field whose value is already in state.answers (back-button friendly).
                    values      = state.Answers
                                       .Where(kvp => step.Fields.Any(f => f.Name == kvp.Key))
                                       .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                    errors      = errors,
                }, ct);
            }

            await EmitStateAsync(ctx.Response, state, ct);
            await WriteAsync(ctx.Response, new RunFinishedEvent(threadId, runId), ct);
        });

        routes.MapGet("/agui/wizard/demo", () => Results.Content(DemoPage, "text/html; charset=utf-8"));
    }

    // ---- Wizard definition -----------------------------------------------------

    private static readonly IReadOnlyList<WizardStep> Steps = new[]
    {
        new WizardStep(
            Id: "shipping",
            Title: "Shipping address",
            Description: "Where should we send this order?",
            Fields: new FormField[]
            {
                new("text",   "fullName", "Full name",  Required: true,  Placeholder: "Ada Lovelace"),
                new("email",  "email",    "Email",      Required: true,  Placeholder: "ada@example.com",
                    Pattern: @"^[^@\s]+@[^@\s]+\.[^@\s]+$"),
                new("text",   "address",  "Street address", Required: true),
                new("select", "country",  "Country",    Required: true,
                    Options: new FormOption[]
                    {
                        new("US", "United States"),
                        new("BD", "Bangladesh"),
                        new("GB", "United Kingdom"),
                        new("DE", "Germany"),
                        new("JP", "Japan"),
                    }),
                new("radio",  "shipping", "Shipping method", Required: true,
                    Options: new FormOption[]
                    {
                        new("standard", "Standard (5–7 days, free)"),
                        new("express",  "Express (2 days, $9.99)"),
                        new("overnight","Overnight ($24.99)"),
                    }),
            }),

        new WizardStep(
            Id: "payment",
            Title: "Payment",
            Description: "How would you like to pay?",
            Fields: new FormField[]
            {
                new("radio", "paymentMethod", "Payment method", Required: true,
                    Options: new FormOption[]
                    {
                        new("card",   "Credit / debit card"),
                        new("paypal", "PayPal"),
                        new("cod",    "Cash on delivery"),
                    }),
                new("text",   "cardName",   "Name on card",  Required: false,
                    Placeholder: "Only required for card payments"),
                new("text",   "cardNumber", "Card number",   Required: false,
                    Placeholder: "4242 4242 4242 4242",
                    Pattern: @"^[\d\s]{12,23}$"),
                new("checkbox","saveCard",  "Save this card for future orders"),
            }),

        new WizardStep(
            Id: "review",
            Title: "Review & confirm",
            Description: "One last check before we place the order.",
            Fields: new FormField[]
            {
                new("number", "quantity", "Quantity", Required: true, Min: 1, Max: 99, DefaultValue: "1"),
                new("textarea","notes",   "Delivery notes (optional)"),
                new("checkbox","accepted","I agree to the terms and conditions", Required: true),
            }),
    };

    // ---- Validation ------------------------------------------------------------

    private static (Dictionary<string, string>? Errors, Dictionary<string, JsonElement>? Cleaned)
        ValidateAndCoerce(WizardStep step, Dictionary<string, JsonElement> values)
    {
        var errors = new Dictionary<string, string>();
        var cleaned = new Dictionary<string, JsonElement>();

        foreach (var field in step.Fields)
        {
            values.TryGetValue(field.Name, out var raw);
            var isEmpty = raw.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                          || (raw.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(raw.GetString()));

            if (field.Required && isEmpty)
            {
                errors[field.Name] = $"{field.Label} is required.";
                continue;
            }
            if (isEmpty) continue;

            switch (field.Kind)
            {
                case "number":
                {
                    if (!double.TryParse(raw.ToString(), out var n))
                    {
                        errors[field.Name] = $"{field.Label} must be a number.";
                        break;
                    }
                    if (field.Min is { } min && n < min) errors[field.Name] = $"{field.Label} must be ≥ {min}.";
                    else if (field.Max is { } max && n > max) errors[field.Name] = $"{field.Label} must be ≤ {max}.";
                    else cleaned[field.Name] = JsonSerializer.SerializeToElement(n, AguiSerializer.Options);
                    break;
                }
                case "checkbox":
                {
                    var b = raw.ValueKind == JsonValueKind.True
                            || (raw.ValueKind == JsonValueKind.String && raw.GetString() is "true" or "on");
                    if (field.Required && !b) errors[field.Name] = $"{field.Label}.";
                    else cleaned[field.Name] = JsonSerializer.SerializeToElement(b, AguiSerializer.Options);
                    break;
                }
                case "select":
                case "radio":
                {
                    var v = raw.GetString();
                    if (field.Options is not null && !field.Options.Any(o => o.Value == v))
                        errors[field.Name] = $"{field.Label}: invalid choice.";
                    else cleaned[field.Name] = raw;
                    break;
                }
                default:
                {
                    if (field.Pattern is not null && !Regex.IsMatch(raw.GetString() ?? "", field.Pattern))
                        errors[field.Name] = $"{field.Label} doesn't look right.";
                    else cleaned[field.Name] = raw;
                    break;
                }
            }
        }

        return errors.Count == 0 ? (null, cleaned) : (errors, null);
    }

    // ---- Order summary (for the final form.complete event) ---------------------

    private static object BuildOrderSummary(IReadOnlyDictionary<string, JsonElement> a)
    {
        string? S(string k) => a.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? N(string k) => a.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        var shippingCost = S("shipping") switch
        {
            "express" => 9.99,
            "overnight" => 24.99,
            _ => 0.0,
        };
        var qty  = (int)(N("quantity") ?? 1);
        var unit = 249.00;
        var subtotal = qty * unit;
        var total = subtotal + shippingCost;

        return new
        {
            customer = new { name = S("fullName"), email = S("email"), country = S("country") },
            address  = S("address"),
            shipping = S("shipping"),
            payment  = S("paymentMethod"),
            line     = new { sku = "AUR-001", name = "Aurora Wireless Headphones", qty, unitPrice = unit },
            totals   = new { subtotal, shippingCost, total },
        };
    }

    // ---- SSE helpers -----------------------------------------------------------

    private static async Task EmitTextAsync(HttpResponse response, string text, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        await WriteAsync(response, new TextMessageStartEvent(id), ct);
        await WriteAsync(response, new TextMessageContentEvent(id, text), ct);
        await WriteAsync(response, new TextMessageEndEvent(id), ct);
    }

    private static async Task EmitCustomAsync(HttpResponse response, string name, object value, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToElement(value, AguiSerializer.Options);
        await WriteAsync(response, new CustomEvent(name, json), ct);
    }

    private static async Task EmitStateAsync(HttpResponse response, WizardState state, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToElement(state, AguiSerializer.Options);
        await WriteAsync(response, new StateSnapshotEvent(json), ct);
    }

    private static async Task WriteAsync(HttpResponse response, AguiEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize<AguiEvent>(evt, AguiSerializer.Options);
        var frame = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await response.Body.WriteAsync(frame, ct);
        await response.Body.FlushAsync(ct);
    }

    // ---- Demo client -----------------------------------------------------------

    private const string DemoPage = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <title>AG-UI wizard demo</title>
          <style>
            :root { --bg:#f8fafc; --card:#fff; --border:#e2e8f0; --muted:#64748b; --ink:#0f172a; --brand:#6366f1; --err:#dc2626; }
            * { box-sizing: border-box; }
            body { font: 14px system-ui, sans-serif; background: var(--bg); color: var(--ink); margin: 0; padding: 32px 16px; }
            main { max-width: 560px; margin: 0 auto; }
            h1 { font-size: 20px; margin: 0 0 4px; }
            p.sub { margin: 0 0 20px; color: var(--muted); }
            .card { background: var(--card); border: 1px solid var(--border); border-radius: 12px; padding: 20px; box-shadow: 0 1px 2px rgba(0,0,0,0.03); }
            .steps { display:flex; gap:6px; margin-bottom: 16px; }
            .steps .dot { flex:1; height:4px; border-radius:2px; background: var(--border); }
            .steps .dot.done, .steps .dot.active { background: var(--brand); }
            label { display: block; margin: 12px 0 4px; font-weight: 500; }
            input[type=text], input[type=email], input[type=number], select, textarea {
              width: 100%; padding: 8px 10px; border: 1px solid var(--border); border-radius: 8px; background:#fff; font: inherit; }
            textarea { min-height: 80px; resize: vertical; }
            .radio, .check { display:flex; align-items:center; gap:8px; padding:6px 0; }
            .err { color: var(--err); font-size: 12px; margin-top: 4px; }
            .actions { display:flex; justify-content:space-between; margin-top: 20px; gap: 8px; }
            button { background: var(--ink); color:#fff; border:0; padding:10px 16px; border-radius:8px; cursor:pointer; font-weight:500; }
            button.secondary { background:#fff; color:var(--ink); border:1px solid var(--border); }
            #log { color: var(--muted); margin: 8px 0 16px; min-height: 18px; }
            pre { background:#f1f5f9; border-radius:8px; padding:12px; overflow:auto; font-size:12px; }
            .summary h3 { margin-top: 0; }
            .row { display:flex; justify-content:space-between; padding:4px 0; border-bottom: 1px dashed var(--border); }
          </style>
        </head>
        <body>
          <main>
            <h1>AG-UI wizard demo</h1>
            <p class="sub">The agent emits form schemas as <code>CUSTOM</code> events. The client renders them, collects values, and POSTs back — state lives on the wire in <code>STATE_SNAPSHOT</code>.</p>
            <div id="log"></div>
            <div id="root" class="card"></div>
          </main>

          <script>
            const root = document.getElementById('root');
            const log  = document.getElementById('log');
            const threadId = crypto.randomUUID();
            let state = { step: 0, answers: {} };
            const history = [];

            start();

            async function start() { await call(null); }

            async function call(submission) {
              log.textContent = '';
              const body = {
                threadId, runId: crypto.randomUUID(),
                state, submission,
              };
              const res = await fetch('/agui/wizard', {
                method: 'POST',
                headers: { 'content-type': 'application/json', 'accept': 'text/event-stream' },
                body: JSON.stringify(body),
              });
              await consume(res);
            }

            async function consume(res) {
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
              if (evt.type === 'STATE_SNAPSHOT')        { state = evt.snapshot; return; }
              if (evt.type !== 'CUSTOM') return;
              if (evt.name === 'form.request')  renderForm(evt.value);
              if (evt.name === 'form.complete') renderComplete(evt.value);
            }

            function renderForm(form) {
              history.push(JSON.parse(JSON.stringify(state)));
              const dots = Array.from({ length: form.totalSteps }, (_, i) => {
                const cls = i + 1 < form.step ? 'done' : (i + 1 === form.step ? 'active' : '');
                return `<div class="dot ${cls}"></div>`;
              }).join('');
              root.innerHTML = `
                <div class="steps">${dots}</div>
                <h3 style="margin:0 0 4px;">${esc(form.title)} <span style="color:var(--muted);font-weight:400;font-size:13px;">— Step ${form.step}/${form.totalSteps}</span></h3>
                <p style="margin:0 0 12px;color:var(--muted);">${esc(form.description || '')}</p>
                <form id="f">${form.fields.map(f => renderField(f, form.values?.[f.name], form.errors?.[f.name])).join('')}</form>
                <div class="actions">
                  <button class="secondary" id="back" ${form.step === 1 ? 'disabled style="visibility:hidden"' : ''}>Back</button>
                  <button id="next">${esc(form.submitLabel || 'Continue')}</button>
                </div>
              `;
              document.getElementById('next').onclick = () => submit(form);
              const back = document.getElementById('back');
              if (back) back.onclick = goBack;
            }

            function renderField(f, value, error) {
              const v = value ?? f.defaultValue ?? '';
              const errHtml = error ? `<div class="err">${esc(error)}</div>` : '';
              switch (f.kind) {
                case 'select':
                  return `<label>${esc(f.label)}</label>
                    <select name="${f.name}">
                      <option value="">— choose —</option>
                      ${f.options.map(o => `<option value="${esc(o.value)}" ${o.value === v ? 'selected' : ''}>${esc(o.label)}</option>`).join('')}
                    </select>${errHtml}`;
                case 'radio':
                  return `<label>${esc(f.label)}</label>
                    ${f.options.map(o => `
                      <div class="radio"><input type="radio" name="${f.name}" value="${esc(o.value)}" ${o.value === v ? 'checked' : ''}/>
                      <span>${esc(o.label)}</span></div>`).join('')}${errHtml}`;
                case 'checkbox':
                  return `<div class="check"><input type="checkbox" name="${f.name}" ${v === true || v === 'true' ? 'checked' : ''}/>
                    <label style="margin:0;">${esc(f.label)}</label></div>${errHtml}`;
                case 'textarea':
                  return `<label>${esc(f.label)}</label><textarea name="${f.name}" placeholder="${esc(f.placeholder || '')}">${esc(v)}</textarea>${errHtml}`;
                case 'number':
                  return `<label>${esc(f.label)}</label><input type="number" name="${f.name}" value="${esc(v)}"
                    ${f.min != null ? `min="${f.min}"` : ''} ${f.max != null ? `max="${f.max}"` : ''}/>${errHtml}`;
                case 'email':
                  return `<label>${esc(f.label)}</label><input type="email" name="${f.name}" value="${esc(v)}" placeholder="${esc(f.placeholder || '')}"/>${errHtml}`;
                default:
                  return `<label>${esc(f.label)}</label><input type="text" name="${f.name}" value="${esc(v)}" placeholder="${esc(f.placeholder || '')}"/>${errHtml}`;
              }
            }

            function submit(form) {
              const values = {};
              for (const f of form.fields) {
                const el = document.querySelector(`#f [name="${f.name}"]`);
                if (!el) continue;
                if (f.kind === 'checkbox') values[f.name] = el.checked;
                else if (f.kind === 'radio') {
                  const checked = document.querySelector(`#f [name="${f.name}"]:checked`);
                  values[f.name] = checked ? checked.value : '';
                }
                else if (f.kind === 'number') values[f.name] = el.value === '' ? null : Number(el.value);
                else values[f.name] = el.value;
              }
              call({ formId: form.formId, values });
            }

            function goBack() {
              const prev = history.pop();
              if (!prev) return;
              state = history.length > 0 ? history.pop() : { step: 0, answers: {} };
              call(null);
            }

            function renderComplete(payload) {
              const s = payload.summary;
              root.innerHTML = `
                <div class="summary">
                  <h3>Order placed</h3>
                  <p style="color:var(--muted);">Thanks ${esc(s.customer?.name || '')}! A confirmation was sent to ${esc(s.customer?.email || '')}.</p>
                  <div class="row"><span>${esc(s.line.name)} × ${s.line.qty}</span><span>$${(s.line.qty * s.line.unitPrice).toFixed(2)}</span></div>
                  <div class="row"><span>Shipping (${esc(s.shipping || '')})</span><span>$${s.totals.shippingCost.toFixed(2)}</span></div>
                  <div class="row" style="font-weight:600;"><span>Total</span><span>$${s.totals.total.toFixed(2)}</span></div>
                  <h4 style="margin-top:16px;">Structured submission</h4>
                  <pre>${esc(JSON.stringify(payload.answers, null, 2))}</pre>
                </div>
              `;
            }

            function esc(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
          </script>
        </body>
        </html>
        """;
}

// ---- Wire types ----------------------------------------------------------------

internal sealed record WizardRunInput(
    string? ThreadId,
    string? RunId,
    WizardState? State,
    WizardSubmission? Submission);

internal sealed record WizardState(int Step, Dictionary<string, JsonElement> Answers);

internal sealed record WizardSubmission(string FormId, Dictionary<string, JsonElement> Values);

// ---- Form schema types ---------------------------------------------------------

internal sealed record WizardStep(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<FormField> Fields);

internal sealed record FormField(
    string Kind,
    string Name,
    string Label,
    bool Required = false,
    string? Placeholder = null,
    string? Pattern = null,
    double? Min = null,
    double? Max = null,
    string? DefaultValue = null,
    IReadOnlyList<FormOption>? Options = null);

internal sealed record FormOption(string Value, string Label);
