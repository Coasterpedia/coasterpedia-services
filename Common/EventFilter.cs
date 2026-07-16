namespace CoasterpediaServices.Common;

// Mirrors SNS filter policy semantics: Schemas/Namespaces are allow-lists,
// ExcludeUsers is the "anything-but" exclusion list.
public record EventFilter
{
    public required IReadOnlyCollection<string> Schemas { get; init; }
    public required IReadOnlyCollection<string> Namespaces { get; init; }
    public IReadOnlyCollection<string> ExcludeUsers { get; init; } = [];

    public bool Matches(EventBusEvent evt)
    {
        if (evt.Schema == null || !Schemas.Contains(evt.Schema))
        {
            return false;
        }

        if (evt.Namespace == null || !Namespaces.Contains(evt.Namespace))
        {
            return false;
        }

        if (evt.User != null && ExcludeUsers.Contains(evt.User))
        {
            return false;
        }

        return true;
    }
}
