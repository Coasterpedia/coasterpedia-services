using System.Text.Json.Nodes;

namespace CoasterpediaServices.Common;

public record EventBusEvent(string? Schema, string? Namespace, string? User, string? PageTitle)
{
    public int? PageId { get; init; }

    /// <summary>The revision the event is about: the new one for revision/create, the page's latest for the rest.</summary>
    public long? RevisionId { get; init; }

    /// <summary>The performer's groups, implicit ones included ("*", "user"). Null when the event does not say.</summary>
    public IReadOnlyList<string>? UserGroups { get; init; }

    public static EventBusEvent? FromJson(JsonNode? eventBody)
    {
        var pageTitle = eventBody?["page_title"]?.ToString();
        if (string.IsNullOrEmpty(pageTitle))
        {
            return null;
        }

        return new EventBusEvent(
            eventBody?["$schema"]?.ToString(),
            eventBody?["page_namespace"]?.ToString(),
            eventBody?["performer"]?["user_text"]?.ToString(),
            pageTitle)
        {
            PageId = int.TryParse(eventBody?["page_id"]?.ToString(), out var id) ? id : null,
            RevisionId = long.TryParse(eventBody?["rev_id"]?.ToString(), out var rev) ? rev : null,
            UserGroups = eventBody?["performer"]?["user_groups"] is JsonArray groups
                ? groups.Select(g => g?.ToString()).OfType<string>().ToList()
                : null,
        };
    }
}
