using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkyMessage.Contracts;

/// <summary>
/// Reads an enum like <see cref="JsonStringEnumConverter"/>, except that a name this build does not know reads as
/// <c>default</c> (the enum's <c>Unknown = 0</c>) instead of failing the whole payload. For enums a newer server adds
/// values to while older apps are still installed: one new service message must not break that chat's page of
/// messages or the chat list. Writing is unchanged.
/// </summary>
public sealed class TolerantEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    private JsonConverter<TEnum>? _strict;

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) return Strict(options).Read(ref reader, typeToConvert, options);
        // A string token is read in place (nothing to skip), so a failed attempt on a copy leaves the reader as it was.
        var attempt = reader;
        try
        {
            return Strict(options).Read(ref attempt, typeToConvert, options);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        Strict(options).Write(writer, value, options);

    private JsonConverter<TEnum> Strict(JsonSerializerOptions options) =>
        _strict ??= (JsonConverter<TEnum>)new JsonStringEnumConverter<TEnum>().CreateConverter(typeof(TEnum), options);
}
