using CoasterpediaServices.Projector.Model;
using CoasterpediaServices.Projector.Vocabulary;

namespace CoasterpediaServices.Projector.Mapping;

/// <summary>
/// Which columns name the park, the owner and the place, for each kind
/// Travelling.classify stored (DR-006, DR-010 §3.1). The kind is never decided
/// here: it is read from LocationKind, and this only projects it, as
/// Travelling.spell does. Shared by the current location and each past stint.
/// </summary>
public static class LocationMapper
{
    public sealed record Projection(Ref? Park, Ref? Owner, string? Place);

    /// <summary>Every link target the columns cite, for one batched resolve.</summary>
    public static IEnumerable<string> LinkTargets(string? park, string? owner) =>
        Wikitext.Refs(park).Concat(Wikitext.Refs(owner)).Select(x => x.Target).OfType<string>();

    /// <param name="kind">LocationKind as stored.</param>
    /// <param name="onLoan">OnLoan as stored: a park's owner is named only when it is someone else.</param>
    public static Projection Project(string? kind, bool? onLoan, string? park, string? owner, string? location,
        string vocabType, Vocabularies vocab, IReadOnlyDictionary<string, int> resolved)
    {
        var parkLink = Wikitext.Refs(park).FirstOrDefault(x => x.Target != null);
        var linkedPark = parkLink.Target == null ? null : Resolve(parkLink, resolved);
        var ownerRef = FirstRef(owner, vocabType, vocab, resolved);
        // Unlinked Park text names who has it when there is no park: a showman, or a stored ride's keeper.
        var who = linkedPark == null && !IsMarker(Wikitext.Strip(park ?? "")) ? FirstRef(park, vocabType, vocab, resolved) : null;
        var place = Place(location, vocabType, vocab);

        return kind switch
        {
            "park" => new Projection(linkedPark, onLoan == true ? ownerRef : null, place),
            "storage" => new Projection(linkedPark, ownerRef ?? who, StoragePlace(location)),
            // A linked company beside a travel marker is the showman, not a park (DR-006).
            "travelling" => new Projection(null, ownerRef ?? who ?? linkedPark, null),
            "standalone" => new Projection(null, ownerRef, place),
            _ => new Projection(null, null, null),
        };
    }

    private static Ref? FirstRef(string? wikitext, string vocabType, Vocabularies vocab, IReadOnlyDictionary<string, int> resolved)
    {
        var x = Wikitext.Refs(wikitext).FirstOrDefault();
        if (x.Ref == null || (x.Target == null && IsPlaceholder(x.Ref.Name, vocabType, vocab)))
        {
            return null;
        }

        return Resolve(x, resolved);
    }

    private static Ref Resolve((string? Target, Ref Ref) x, IReadOnlyDictionary<string, int> resolved) =>
        x.Target != null && resolved.TryGetValue(x.Target, out var id) ? x.Ref with { Id = id } : x.Ref;

    /// <summary>Location as written, unless it is a marker or a placeholder: then it names no place.</summary>
    private static string? Place(string? location, string vocabType, Vocabularies vocab)
    {
        var text = Wikitext.Strip(location ?? "");
        return text.Length == 0 || IsMarker(text) || IsStorage(text) || IsPlaceholder(text, vocabType, vocab) ? null : text;
    }

    /// <summary>The place a storage marker names after a comma ("In storage, Middle East"); any other text after it is a note.</summary>
    private static string? StoragePlace(string? location)
    {
        var text = Wikitext.Strip(location ?? "");
        var comma = text.IndexOf(',');
        var place = comma < 0 ? "" : text[(comma + 1)..].Trim();
        return place.Length == 0 ? null : place;
    }

    // Module:Travelling's markers and placeholders, read the same way.
    private static bool IsMarker(string text) =>
        text.StartsWith("travel", StringComparison.OrdinalIgnoreCase)
        || string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c != '-')).Equals("standalone", StringComparison.OrdinalIgnoreCase);

    private static bool IsStorage(string text) =>
        text.StartsWith("in storage", StringComparison.OrdinalIgnoreCase) || text.StartsWith("stored", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlaceholder(string text, string vocabType, Vocabularies vocab) =>
        text.Equals("unknown", StringComparison.OrdinalIgnoreCase)
        || text.Equals("owner unknown", StringComparison.OrdinalIgnoreCase)
        || (vocab.Match(vocabType, "country", text) is { } country && !country.Equals("ship", StringComparison.OrdinalIgnoreCase));
}
