using System.Text.Json;

namespace BeautyKit;

/// <summary>
/// The suggestions shown while typing a search come from the engine the user picked. Each one has its own endpoint;
/// all answer with <c>["query", ["suggestion", …]]</c> except Bing, whose JSON lives under <c>AS.Results[].Suggests[].Txt</c>
/// (its OpenSearch endpoint, osjson.aspx, answers 204 with nothing).
/// </summary>
public static class SearchSuggestions
{
    /// <summary>The URL that returns suggestions for <paramref name="query"/> with that engine (Google when the name is unknown).</summary>
    public static string Url(string engine, string query, string language)
    {
        var q = Uri.EscapeDataString(query);
        if (string.Equals(engine, "DuckDuckGo", StringComparison.OrdinalIgnoreCase)) return "https://duckduckgo.com/ac/?type=list&q=" + q;
        if (string.Equals(engine, "Bing", StringComparison.OrdinalIgnoreCase)) return "https://api.bing.com/qsonhs.aspx?type=json&q=" + q;
        if (string.Equals(engine, "Ecosia", StringComparison.OrdinalIgnoreCase)) return "https://ac.ecosia.org/autocomplete?type=list&q=" + q;
        return "https://suggestqueries.google.com/complete/search?client=firefox&hl=" + Uri.EscapeDataString(language) + "&ie=utf-8&oe=utf-8&q=" + q;
    }

    /// <summary>The suggestions in an engine's answer; empty when the answer is not in a known shape.</summary>
    public static List<string> Parse(string json)
    {
        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 1 && root[1].ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root[1].EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text) result.Add(text);
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("AS", out var answer) &&
                     answer.TryGetProperty("Results", out var groups) && groups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in groups.EnumerateArray())
                {
                    if (!group.TryGetProperty("Suggests", out var suggests) || suggests.ValueKind != JsonValueKind.Array) continue;
                    foreach (var suggest in suggests.EnumerateArray())
                        if (suggest.TryGetProperty("Txt", out var txt) && txt.GetString() is { Length: > 0 } text) result.Add(text);
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON (an error page): no suggestions.
        }
        return result;
    }
}
