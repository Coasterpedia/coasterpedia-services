namespace CoasterpediaServices.ArchiveBot;

// What one run changed in a page's citations, for its edit summary. A citation can count twice: an
// archive link added to a link that is also marked dead.
public sealed class CitationChanges
{
    public int Archived { get; set; }
    public int MarkedDead { get; set; }
    public int MarkedLive { get; set; }
    public int Updated { get; set; }

    // For example "Added archive links to 3 citations and marked 1 link as dead".
    public string Summary()
    {
        var parts = new List<string>();
        if (Archived > 0)
        {
            parts.Add(Archived == 1 ? "added an archive link to 1 citation" : $"added archive links to {Archived} citations");
        }

        if (MarkedDead > 0)
        {
            parts.Add(MarkedDead == 1 ? "marked 1 link as dead" : $"marked {MarkedDead} links as dead");
        }

        if (MarkedLive > 0)
        {
            parts.Add(MarkedLive == 1 ? "marked 1 link as live again" : $"marked {MarkedLive} links as live again");
        }

        if (Updated > 0)
        {
            var other = parts.Count > 0 ? "other " : "";
            parts.Add(Updated == 1 ? $"updated 1 {other}citation" : $"updated {Updated} {other}citations");
        }

        if (parts.Count == 0)
        {
            return "Updated citations";
        }

        var text = parts.Count == 1 ? parts[0] : string.Join(", ", parts[..^1]) + " and " + parts[^1];
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}

public record RewriteResult(string Content, CitationChanges Changes);
