using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkyMessage.Contracts;

/// <summary>
/// JSON settings used everywhere (REST, SignalR, client caches): web defaults (camelCase)
/// plus enums as strings. Server and clients must both call <see cref="Configure"/>.
/// Enums that grow with the server (<see cref="SystemEventType"/>) read unknown names as <c>Unknown</c>.
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
        // Ahead of the general enum converter: the first converter that fits a type is the one used.
        if (!options.Converters.Any(c => c is TolerantEnumConverter<SystemEventType>))
            options.Converters.Insert(0, new TolerantEnumConverter<SystemEventType>());
        if (!options.Converters.Any(c => c is JsonStringEnumConverter))
            options.Converters.Add(new JsonStringEnumConverter());
    }
}
