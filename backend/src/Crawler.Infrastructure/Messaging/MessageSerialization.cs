using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crawler.Infrastructure.Messaging;

public static class MessageSerialization
{
    /// <summary>Wire format for broker messages: camelCase, enums as strings (so the schema survives enum reordering).</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static byte[] Serialize<T>(T message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);
}
