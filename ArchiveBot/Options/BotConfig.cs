namespace CoasterpediaServices.ArchiveBot.Options;

public record BotConfig(
    List<string> CitationTemplates,
    Dictionary<string, string>? SiteConfig,
    List<string>? ImageSourceTemplates
)
{
    // The rule for a host, from the most specific domain listed: "facebook.com" covers www. and m.,
    // and "m.facebook.com" would override it. Matched on whole labels, so it never covers "notfacebook.com".
    public string? SiteRule(string host)
    {
        if (SiteConfig == null)
        {
            return null;
        }

        var domain = host.TrimEnd('.');
        while (true)
        {
            var match = SiteConfig.FirstOrDefault(x => x.Key.Equals(domain, StringComparison.OrdinalIgnoreCase));
            if (match.Key != null)
            {
                return match.Value;
            }

            var dot = domain.IndexOf('.');
            if (dot < 0)
            {
                return null;
            }

            domain = domain[(dot + 1)..];
        }
    }
}
