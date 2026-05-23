using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace EShop.AgenticAI.Mcp;

internal sealed class McpClientAccessor : IAsyncDisposable
{
    private readonly Lazy<Task<McpSession>> _session;

    public McpClientAccessor(IConfiguration config, ILoggerFactory loggerFactory)
    {
        _session = new Lazy<Task<McpSession>>(
            () => CreateAsync(config, loggerFactory),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<McpSession> GetAsync() => _session.Value;

    private static async Task<McpSession> CreateAsync(IConfiguration config, ILoggerFactory loggerFactory)
    {
        var endpoint =
            config["services:mcp:https:0"] ??
            config["services:mcp:http:0"] ??
            throw new InvalidOperationException(
                "No MCP service endpoint found in configuration. Expected 'services:mcp:https:0' or 'services:mcp:http:0' (injected by Aspire WithReference(mcp)).");

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(endpoint),
                TransportMode = HttpTransportMode.AutoDetect,
                Name = "EShop.AgenticAI",
            },
            loggerFactory);

        var client = await McpClient.CreateAsync(transport, clientOptions: null, loggerFactory: loggerFactory);
        var tools = await client.ListToolsAsync();
        return new McpSession(client, tools);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session.IsValueCreated)
        {
            var session = await _session.Value.ConfigureAwait(false);
            await session.Client.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed record McpSession(McpClient Client, IList<McpClientTool> Tools)
{
    public IList<AITool> AsAITools() => Tools.Cast<AITool>().ToList();
}
