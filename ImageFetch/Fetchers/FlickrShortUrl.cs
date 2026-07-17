using System.Numerics;

namespace CoasterpediaServices.ImageFetch.Fetchers;

// flic.kr short codes are base58 encodings of the target's numeric Flickr ID
// (photo, photoset, etc.) - not aliases that need a server-side lookup.
internal static class FlickrShortUrl
{
    private const string Alphabet = "123456789abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ";

    public static string Decode(string code)
    {
        var value = BigInteger.Zero;
        foreach (var c in code)
        {
            var digit = Alphabet.IndexOf(c);
            if (digit < 0)
            {
                throw new ImageFetchException(400, "Unrecognised Flickr short URL code.");
            }

            value = value * Alphabet.Length + digit;
        }

        return value.ToString();
    }
}
