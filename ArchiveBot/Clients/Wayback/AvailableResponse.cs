namespace CoasterpediaServices.ArchiveBot.Clients.Wayback;

public record AvailableResponse(
    string Url,
    ArchivedSnapshots? ArchivedSnapshots
);