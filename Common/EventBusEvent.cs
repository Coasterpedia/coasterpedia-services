using System.Text.Json.Nodes;

namespace CoasterpediaServices.Common;

public record EventBusEvent(string? Schema, string? Namespace, string? User, string? PageTitle)
{
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
            pageTitle);
    }
}
