using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace EShop.McpServer.Prompts;

[McpServerPromptType]
public static class AnalyzeSalesTrendPrompt
{
    private const string SystemInstruction =
        "You are an ecommerce analytics assistant. Always ground every claim in numbers " +
        "returned by MCP tools — never invent figures. Use UTC dates. " +
        "Respond in two parts: (1) a 3-5 sentence narrative summary, then (2) a Markdown table " +
        "of the underlying bucketed data. Round currency to 2dp; flag week-over-week changes " +
        "above +/-10% as 'notable'.";

    [McpServerPrompt(Name = "analyze-sales-trend")]
    [Description("Narrate the sales trend across a date range using GetSalesSummary. Returns a summary + bucketed table.")]
    public static IList<ChatMessage> Build(
        [Description("Start date inclusive in yyyy-MM-dd (UTC). Leave empty to default to 30 days before 'to'.")] string? from = null,
        [Description("End date inclusive in yyyy-MM-dd (UTC). Leave empty to default to today.")] string? to = null,
        [Description("Bucket granularity: Day, Week, or Month. Defaults to Week.")] string granularity = "Week")
    {
        var range = FormatRange(from, to);
        var bucket = string.IsNullOrWhiteSpace(granularity) ? "Week" : granularity.Trim();

        var user =
            $"Analyze the sales trend for {range}, bucketed by {bucket}.\n\n" +
            "Steps:\n" +
            $"1. Call the MCP tool `GetSalesSummary` with from={FormatDate(from)}, to={FormatDate(to)}, groupBy={bucket}.\n" +
            "2. Identify the overall direction (up/down/flat), the strongest and weakest buckets, and any notable swing.\n" +
            "3. If the range spans more than 60 days, also call `GetSalesSummary` with groupBy=Month to corroborate the trend at a coarser grain (mention briefly if it agrees).\n\n" +
            "Output shape:\n" +
            "- A 3-5 sentence summary covering: total revenue, total orders, AOV trend, and the most notable bucket.\n" +
            "- A Markdown table with columns: Bucket Start, Revenue, Orders, AOV, vs Prior (%).";

        return new List<ChatMessage>
        {
            new(ChatRole.System, SystemInstruction),
            new(ChatRole.User, user),
        };
    }

    private static string FormatDate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(default)" : value.Trim();

    private static string FormatRange(string? from, string? to)
    {
        var hasFrom = !string.IsNullOrWhiteSpace(from);
        var hasTo = !string.IsNullOrWhiteSpace(to);
        return (hasFrom, hasTo) switch
        {
            (true, true)  => $"{from} through {to}",
            (true, false) => $"{from} through today",
            (false, true) => $"the 30 days ending {to}",
            _             => "the last 30 days",
        };
    }
}
