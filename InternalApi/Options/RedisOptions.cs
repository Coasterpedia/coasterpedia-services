namespace CoasterpediaServices.InternalApi.Options;

public record RedisOptions
{
    public required string ConnectionString { get; init; }
    public int Db { get; init; }
}
