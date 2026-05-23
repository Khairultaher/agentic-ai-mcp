using Azure.AI.OpenAI;
using EShop.AgenticAI.Mcp;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureOpenAIClient("openai");

// MCP client (long-lived, lazily initialized on first use).
builder.Services.AddSingleton<McpClientAccessor>();

// IChatClient backed by Azure OpenAI with function-invocation middleware so MCP tools auto-execute.
var deploymentName = builder.Configuration["AzureOpenAI:Deployment"] ?? "gpt-4o";
builder.Services.AddChatClient(sp =>
    {
        var azure = sp.GetRequiredService<AzureOpenAIClient>();
        return azure.GetChatClient(deploymentName).AsIChatClient();
    })
    .UseFunctionInvocation();

// Register an AIAgent backed by the chat client + MCP tools so DevUI surfaces it as a playground.
builder.Services.AddAIAgent("EShopAnalyst", (sp, name) =>
{
    var chat = sp.GetRequiredService<IChatClient>();
    var mcp = sp.GetRequiredService<McpClientAccessor>().GetAsync().GetAwaiter().GetResult();
    return new ChatClientAgent(
        chat,
        instructions: "You are an ecommerce analytics assistant. Use the MCP tools to answer questions about sales, " +
                      "stock, customer growth, and zone profit. Always ground every number you cite in a tool result. " +
                      "Respond with a short narrative summary followed by a Markdown table when data is tabular.",
        name: name,
        description: "Ecom analytics agent over the EShop MCP server.",
        tools: mcp.AsAITools(),
        loggerFactory: sp.GetRequiredService<ILoggerFactory>(),
        services: sp);
});

// DevUI prerequisites (Responses + Conversations APIs, then the DevUI itself).
builder.Services.AddOpenAIResponses();
builder.Services.AddOpenAIConversations();
builder.Services.AddDevUI();

var app = builder.Build();

app.MapDefaultEndpoints();

// ---- POST /ask --------------------------------------------------------------
app.MapPost("/ask", async (
    AskRequest request,
    McpClientAccessor mcpAccessor,
    IChatClient chat,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        return Results.BadRequest(new { error = "Question is required." });
    }

    var session = await mcpAccessor.GetAsync();
    var messages = new List<ChatMessage> { new(ChatRole.User, request.Question) };
    var response = await chat.GetResponseAsync(
        messages,
        new ChatOptions { Tools = session.AsAITools() },
        ct);

    return Results.Ok(new AskResponse(
        Answer: response.Text,
        Trace: ExtractTrace(response)));
});

// ---- POST /prompt/{name} ---------------------------------------------------
app.MapPost("/prompt/{name}", async (
    string name,
    PromptRequest? request,
    McpClientAccessor mcpAccessor,
    IChatClient chat,
    CancellationToken ct) =>
{
    var session = await mcpAccessor.GetAsync();

    Dictionary<string, object?> args = request?.Arguments is { Count: > 0 }
        ? request.Arguments.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value)
        : new Dictionary<string, object?>();

    GetPromptResult prompt;
    try
    {
        prompt = await session.Client.GetPromptAsync(name, args, cancellationToken: ct);
    }
    catch (Exception ex)
    {
        return Results.NotFound(new { error = $"Prompt '{name}' could not be loaded: {ex.Message}" });
    }

    var messages = prompt.ToChatMessages().ToList();
    var response = await chat.GetResponseAsync(
        messages,
        new ChatOptions { Tools = session.AsAITools() },
        ct);

    return Results.Ok(new AskResponse(
        Answer: response.Text,
        Trace: ExtractTrace(response)));
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenAIResponses();
    app.MapOpenAIConversations();
    app.MapDevUI();
}

app.Run();

static IReadOnlyList<ToolTrace> ExtractTrace(ChatResponse response)
{
    var trace = new List<ToolTrace>();
    foreach (var message in response.Messages)
    {
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case FunctionCallContent call:
                    trace.Add(new ToolTrace("call", call.Name, call.Arguments));
                    break;
                case FunctionResultContent result:
                    trace.Add(new ToolTrace("result", result.CallId, result.Result));
                    break;
            }
        }
    }
    return trace;
}

internal sealed record AskRequest(string Question);
internal sealed record PromptRequest(Dictionary<string, string?>? Arguments);
internal sealed record AskResponse(string Answer, IReadOnlyList<ToolTrace> Trace);
internal sealed record ToolTrace(string Kind, string? Name, object? Payload);
