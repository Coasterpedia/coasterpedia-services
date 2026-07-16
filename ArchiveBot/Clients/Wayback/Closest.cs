namespace CoasterpediaServices.ArchiveBot.Clients.Wayback;

public record Closest(
    string Status,
    bool Available,
    string Url,
    string Timestamp
);