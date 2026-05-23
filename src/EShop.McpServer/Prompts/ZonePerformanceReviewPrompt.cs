using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace EShop.McpServer.Prompts;

[McpServerPromptType]
public static class ZonePerformanceReviewPrompt
{
    private const string SystemInstruction =
        "You are a regional performance analyst. Compare zones strictly on the numbers " +
        "returned by MCP tools. Group zones by Region when calling out winners and laggards. " +
        "Use UTC dates and round currency to 2dp. Respond in two parts: " +
        "(1) a 4-6 sentence summary that names the top and bottom region by gross profit and " +
        "the single best and worst zone overall, then (2) a Markdown table.";

    [McpServerPrompt(Name = "zone-performance-review")]
    [Description("Compare gross profit and margin across zones (rolled up by region) for a date range using GetProfitByZone.")]
    public static IList<ChatMessage> Build(
        [Description("Start date inclusive in yyyy-MM-dd (UTC). Leave empty to default to 30 days before 'to'.")] string? from = null,
        [Description("End date inclusive in yyyy-MM-dd (UTC). Leave empty to default to today.")] string? to = null)
    {
        var range = FormatRange(from, to);

        var user =
            $"Run a zone performance review for {range}.\n\n" +
            "Steps:\n" +
            $"1. Call the MCP tool `GetProfitByZone` with from={FormatDate(from)}, to={FormatDate(to)}.\n" +
            "2. Roll the rows up by Region (sum revenue, cost, grossProfit; recompute marginPercent = grossProfit / revenue * 100).\n" +
            "3. Identify the top and bottom region by grossProfit, and the single best and worst zone.\n" +
            "4. Flag any zone whose marginPercent is more than 5 percentage points away from its region average.\n\n" +
            "Output shape:\n" +
            "- A 4-6 sentence summary covering: total gross profit across all zones, top and bottom region, best and worst zone, and any margin outliers from step 4.\n" +
            "- A Markdown table with columns: Region, Zone, Revenue, Cost, Gross Profit, Margin %.\n" +
            "  Sort by Region, then by Gross Profit descending within each region.";

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
