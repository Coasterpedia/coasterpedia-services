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

    public Vocabularies(Dictionary<string, Dictionary<string, HashSet<string>>> values)
    {
        _values = values;
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
}
