using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoasterpediaServices.ImageFetch.Clients.Flickr;

/// <summary>
/// Reads an int that Flickr may serialise as either a JSON number or a JSON string.
/// The REST endpoint is not consistent about this between (or even within) methods -
/// getSizes has historically returned width/height both ways - and the existing models
/// dodge it by typing everything as string. The rotation/size numbers are arithmetic
/// rather than passthrough, so they get parsed here instead.
/// </summary>
internal class FlickrLooseIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt32(),
            JsonTokenType.String when int.TryParse(reader.GetString(), out var parsed) => parsed,
            _ => 0
        };

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}
