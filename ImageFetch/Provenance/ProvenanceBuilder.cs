namespace CoasterpediaServices.ImageFetch.Provenance;

/// <summary>The resolved file-page provenance for a fetched image: Cargo <c>Source</c> label +
/// <c>Licence</c> short, and the ordered notice cards.</summary>
public sealed record ResolvedProvenance(string Source, string License, IReadOnlyList<string> Cards);

/// <summary>
/// Assembles the file-page provenance for a fetched image. This is the whole of what the gadget
/// writes for a URL import — the frontend passes <see cref="ResolvedProvenance"/> straight through,
/// deriving nothing. Card order is: source-attribution card, then the licence card (unless the
/// source template is self-licensing), then any source-specific extras (e.g. a Panoramio mirror).
/// </summary>
public static class ProvenanceBuilder
{
    public static ResolvedProvenance Build(string sourceKey, string slug, string sourceUrl,
        IEnumerable<string>? extraCards = null)
    {
        var source = SourceRegistry.Get(sourceKey);
        var licence = LicenseCatalog.Get(slug)
            ?? throw new ImageFetchException(422, $"Unsupported licence '{slug}'; it can't be imported.");

        var cards = new List<string> { $"{{{{{source.CardTemplate}|{sourceUrl}}}}}" };
        if (!source.SelfLicensing)
        {
            cards.Add(licence.Card);
        }
        if (extraCards != null)
        {
            cards.AddRange(extraCards);
        }

        return new ResolvedProvenance(source.Label, licence.Short, cards);
    }
}
