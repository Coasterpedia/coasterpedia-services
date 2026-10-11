namespace CoasterpediaServices.Projector.Vocabulary;

/// <summary>
/// The wiki's allowed values at one moment, per attraction type and parameter,
/// as TemplateData publishes them (each infobox's suggestedvalues). Validity
/// comes from here, never from the contract (DR-010 §6.2).
/// </summary>
public sealed class Vocabularies
{
    // type → param → allowed values (case-insensitive)
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _values;

    // The same, in TemplateData's order, which is the order the infobox offers them.
    private readonly Dictionary<string, Dictionary<string, List<string>>> _ordered;

    public Vocabularies(Dictionary<string, Dictionary<string, List<string>>> ordered)
    {
        _ordered = ordered;
        _values = ordered.ToDictionary(t => t.Key, t => t.Value.ToDictionary(
            p => p.Key, p => new HashSet<string>(p.Value, StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>The wiki's own spelling of the value, or null when the list does not hold it.</summary>
    public string? Match(string type, string param, string value)
    {
        if (!_values.TryGetValue(type, out var byParam) || !byParam.TryGetValue(param, out var allowed))
        {
            return null;
        }

        return allowed.TryGetValue(value.Trim(), out var actual) ? actual : null;
    }

    public bool Has(string type, string param) =>
        _values.TryGetValue(type, out var byParam) && byParam.ContainsKey(param);

    public IReadOnlyDictionary<string, Dictionary<string, HashSet<string>>> All => _values;

    /// <summary>The allowed values in the infobox's own order; empty when the type has no such list.</summary>
    public IReadOnlyList<string> Ordered(string type, string param) =>
        _ordered.TryGetValue(type, out var byParam) && byParam.TryGetValue(param, out var values) ? values : [];
}
