using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkyMessage.Contracts;

/// <summary>
/// JSON settings used everywhere (REST, SignalR, client caches): web defaults (camelCase)
/// plus enums as strings. Server and clients must both call <see cref="Configure"/>.
/// </summary>
public static class SkyJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        return options;
    }

    public static void Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        if (!options.Converters.Any(c => c is JsonStringEnumConverter))
            options.Converters.Add(new JsonStringEnumConverter());
    }
}
