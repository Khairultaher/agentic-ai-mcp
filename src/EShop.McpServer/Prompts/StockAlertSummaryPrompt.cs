using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace EShop.McpServer.Prompts;

[McpServerPromptType]
public static class StockAlertSummaryPrompt
{
    private const int DefaultThreshold = 300;

    private const string SystemInstruction =
        "You are an inventory operations assistant. Only report facts returned by MCP " +
        "tools and resources — never invent product names or stock levels. " +
        "Prioritise products with the lowest days-of-cover. Respond in two parts: " +
        "(1) a 3-5 sentence executive summary, then (2) a Markdown table of the at-risk products.";

    [McpServerPrompt(Name = "stock-alert-summary")]
    [Description("Summarise products at or below a safety-stock threshold and rank them by days-of-cover.")]
    public static IList<ChatMessage> Build(
        [Description("Stock threshold (units). Products with on-hand at or below this number are flagged. Defaults to 300.")] int threshold = DefaultThreshold)
    {
        var t = threshold <= 0 ? DefaultThreshold : threshold;

        var user =
            $"Produce a low-stock alert summary using threshold={t}.\n\n" +
            "Steps:\n" +
            "1. Read the MCP resource `reports://low-stock` for a pre-shaped baseline snapshot (cover threshold = 300).\n" +
            $"2. Call the MCP tool `GetStockLevels` with belowThreshold={t} to get the live cut at the requested threshold.\n" +
            "3. Treat the tool result as authoritative; use the resource only to corroborate.\n" +
            "4. Rank items by daysOfCover ascending (null = no recent usage, sort last).\n\n" +
            "Output shape:\n" +
            "- A 3-5 sentence summary covering: how many products are at risk, the single most-at-risk product, and any category that is over-represented.\n" +
            "- A Markdown table with columns: Product, Category, On Hand, Avg Daily Usage, Days of Cover.\n" +
            "  Cap the table at the 15 worst items; mention the truncation if more exist.";

        return new List<ChatMessage>
        {
            new(ChatRole.System, SystemInstruction),
            new(ChatRole.User, user),
        };
    }
}
