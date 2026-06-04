using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace EShop.AgenticAI.Mcp;

internal sealed class SlashCommandChatClient(IChatClient inner, McpClientAccessor mcpAccessor)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (TryGetSlashCommand(messages, out var command))
        {
            var text = await HandleAsync(command, cancellationToken).ConfigureAwait(false);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
        }
        return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (TryGetSlashCommand(messages, out var command))
        {
            var text = await HandleAsync(command, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            yield break;
        }

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private static bool TryGetSlashCommand(IEnumerable<ChatMessage> messages, out string command)
    {
        var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User);
        var text = lastUser?.Text?.TrimStart();
        if (!string.IsNullOrEmpty(text) && text.StartsWith('/'))
        {
            command = text;
            return true;
        }
        command = string.Empty;
        return false;
    }

    private async Task<string> HandleAsync(string command, CancellationToken ct)
    {
        var space = command.IndexOf(' ');
        var verb = (space < 0 ? command : command[..space]).ToLowerInvariant();
        var arg = space < 0 ? string.Empty : command[(space + 1)..].Trim();

        try
        {
            return verb switch
            {
                "/help" => HelpText,
                "/resources" => await ListResourcesAsync(ct).ConfigureAwait(false),
                "/resource" => string.IsNullOrEmpty(arg)
                    ? "Usage: `/resource <uri>` — e.g. `/resource schema://ecom`"
                    : await ReadResourceAsync(arg, ct).ConfigureAwait(false),
                "/prompts" => await ListPromptsAsync(ct).ConfigureAwait(false),
                "/prompt" => string.IsNullOrEmpty(arg)
                    ? "Usage: `/prompt <name> [key=value ...]` — e.g. `/prompt analyze-sales-trend from=2026-01-01 to=2026-03-31`"
                    : await GetPromptAsync(arg, ct).ConfigureAwait(false),
                _ => $"Unknown command `{verb}`. Type `/help` to see available commands.",
            };
        }
        catch (Exception ex)
        {
            return $"Command `{verb}` failed: {ex.Message}";
        }
    }

    private async Task<string> ListResourcesAsync(CancellationToken ct)
    {
        var session = await mcpAccessor.GetAsync().ConfigureAwait(false);
        var sb = new StringBuilder();

        var statics = await session.Client.ListResourcesAsync(cancellationToken: ct).ConfigureAwait(false);
        sb.AppendLine("**Static resources**");
        sb.AppendLine();
        if (statics.Count == 0)
        {
            sb.AppendLine("_(none)_");
        }
        else
        {
            sb.AppendLine("| URI | Name | Description |");
            sb.AppendLine("|---|---|---|");
            foreach (var r in statics)
            {
                sb.AppendLine($"| `{r.Uri}` | {r.Name} | {Escape(r.Description)} |");
            }
        }

        var templates = await session.Client.ListResourceTemplatesAsync(cancellationToken: ct).ConfigureAwait(false);
        sb.AppendLine();
        sb.AppendLine("**Resource templates**");
        sb.AppendLine();
        if (templates.Count == 0)
        {
            sb.AppendLine("_(none)_");
        }
        else
        {
            sb.AppendLine("| URI Template | Name | Description |");
            sb.AppendLine("|---|---|---|");
            foreach (var t in templates)
            {
                sb.AppendLine($"| `{t.UriTemplate}` | {t.Name} | {Escape(t.Description)} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Load one with `/resource <uri>` — e.g. `/resource schema://ecom` or `/resource reports://daily-sales/2026-05-23`.");

        return sb.ToString();
    }

    private async Task<string> ReadResourceAsync(string uri, CancellationToken ct)
    {
        var session = await mcpAccessor.GetAsync().ConfigureAwait(false);
        var result = await session.Client.ReadResourceAsync(uri, cancellationToken: ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine($"**Resource:** `{uri}`");
        sb.AppendLine();

        foreach (var content in result.Contents)
        {
            switch (content)
            {
                case TextResourceContents text:
                    var mime = text.MimeType ?? "text/plain";
                    var lang = mime.Contains("json", StringComparison.OrdinalIgnoreCase) ? "json"
                             : mime.Contains("xml", StringComparison.OrdinalIgnoreCase) ? "xml"
                             : "";
                    sb.AppendLine($"```{lang}");
                    sb.AppendLine(text.Text);
                    sb.AppendLine("```");
                    break;
                case BlobResourceContents blob:
                    sb.AppendLine($"_(binary content, {blob.MimeType ?? "unknown MIME type"}, base64 length {blob.Blob.Length})_");
                    break;
            }
        }

        return sb.ToString();
    }

    private async Task<string> ListPromptsAsync(CancellationToken ct)
    {
        var session = await mcpAccessor.GetAsync().ConfigureAwait(false);
        var prompts = await session.Client.ListPromptsAsync(cancellationToken: ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine("**Available prompts**");
        sb.AppendLine();
        if (prompts.Count == 0)
        {
            sb.AppendLine("_(none)_");
            return sb.ToString();
        }

        sb.AppendLine("| Name | Description | Arguments |");
        sb.AppendLine("|---|---|---|");
        foreach (var p in prompts)
        {
            var args = p.ProtocolPrompt.Arguments is { Count: > 0 } argList
                ? string.Join(", ", argList.Select(a =>
                    a.Required == true ? $"`{a.Name}` (required)" : $"`{a.Name}`"))
                : "_(none)_";
            sb.AppendLine($"| `{p.Name}` | {Escape(p.Description)} | {args} |");
        }

        sb.AppendLine();
        sb.AppendLine("Render one with `/prompt <name> [key=value ...]` — e.g. `/prompt zone-performance-review`.");
        return sb.ToString();
    }

    private async Task<string> GetPromptAsync(string arg, CancellationToken ct)
    {
        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = tokens[0];
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens.Skip(1))
        {
            var eq = token.IndexOf('=');
            if (eq > 0)
            {
                args[token[..eq]] = token[(eq + 1)..];
            }
        }

        var session = await mcpAccessor.GetAsync().ConfigureAwait(false);
        var result = await session.Client.GetPromptAsync(name, args, cancellationToken: ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine($"**Prompt:** `{name}`");
        if (!string.IsNullOrEmpty(result.Description))
        {
            sb.AppendLine();
            sb.AppendLine(result.Description);
        }
        sb.AppendLine();

        foreach (var message in result.Messages)
        {
            sb.AppendLine($"**{message.Role}**");
            sb.AppendLine();
            switch (message.Content)
            {
                case TextContentBlock text:
                    sb.AppendLine(text.Text);
                    break;
                default:
                    sb.AppendLine($"_(content type: {message.Content?.GetType().Name ?? "null"})_");
                    break;
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Escape(string? s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace("|", "\\|").Replace("\n", " ");

    private const string HelpText = """
        **Available slash commands**

        - `/help` — show this help.
        - `/resources` — list all MCP resources (static + templates).
        - `/resource <uri>` — load a resource and print its content. Examples:
          - `/resource schema://ecom`
          - `/resource reports://low-stock`
          - `/resource reports://daily-sales/2026-05-23`
        - `/prompts` — list all MCP prompts.
        - `/prompt <name> [key=value ...]` — render an MCP prompt with optional arguments. Examples:
          - `/prompt zone-performance-review`
          - `/prompt analyze-sales-trend from=2026-01-01 to=2026-03-31`

        **AG-UI demos** (open in a browser)

        - [Card](http://localhost:5038/agui/demo)
        - [Dashboard](http://localhost:5038/agui/dashboard/demo)
        - [Wizard](http://localhost:5038/agui/wizard/demo)

        Type anything else to chat with the agent as usual.
        """;
}
