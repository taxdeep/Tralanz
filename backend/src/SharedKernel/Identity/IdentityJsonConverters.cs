using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharedKernel.Identity;

/// <summary>
/// JSON converters that emit/read CompanyId, UserId, and EntityNumber as
/// plain strings (e.g. "C000001", "U000001", "EN20260000001"). Without
/// these the default System.Text.Json behaviour wraps each one in a
/// `{ "value": "..." }` object — wrong for the wire and breaks every
/// consumer that expects a bare string id.
/// </summary>
public sealed class CompanyIdJsonConverter : JsonConverter<CompanyId>
{
    public override CompanyId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return default;
        }
        var text = reader.GetString();
        if (string.IsNullOrEmpty(text))
        {
            return default;
        }
        return CompanyId.Parse(text);
    }

    public override void Write(Utf8JsonWriter writer, CompanyId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value ?? string.Empty);
    }
}

public sealed class UserIdJsonConverter : JsonConverter<UserId>
{
    public override UserId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return default;
        }
        var text = reader.GetString();
        if (string.IsNullOrEmpty(text))
        {
            return default;
        }
        return UserId.Parse(text);
    }

    public override void Write(Utf8JsonWriter writer, UserId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value ?? string.Empty);
    }
}

public sealed class EntityNumberJsonConverter : JsonConverter<EntityNumber>
{
    public override EntityNumber Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return default;
        }
        var text = reader.GetString();
        if (string.IsNullOrEmpty(text))
        {
            return default;
        }
        return EntityNumber.Parse(text);
    }

    public override void Write(Utf8JsonWriter writer, EntityNumber value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value ?? string.Empty);
    }
}
