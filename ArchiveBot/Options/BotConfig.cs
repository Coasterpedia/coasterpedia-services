namespace CoasterpediaServices.ArchiveBot.Options;

public record BotConfig(
    List<string> CitationTemplates,
    Dictionary<string, string> SiteConfig
);