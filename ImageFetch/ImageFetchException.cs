namespace CoasterpediaServices.ImageFetch;

public class ImageFetchException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
