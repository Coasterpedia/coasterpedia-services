namespace CoasterpediaServices.Common.Options;

public record CoasterpediaConfig
{
    public required string BaseUrl { get; init; }
}