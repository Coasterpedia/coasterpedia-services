namespace CoasterpediaServices.ArchiveBot.Clients.WebClient;

public record StatusResponse(
    bool? Available, HttpResponseMessage? ResponseMessage);