using System.Text.Json;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

/// <summary>
/// The app keeps reading what a newer server sends: a service message type added after this build reads as
/// <see cref="SystemEventType.Unknown"/> instead of failing the whole page of messages.
/// </summary>
public sealed class SkyJsonTests
{
    private static string Page(params string[] systemTypes)
    {
        var messages = systemTypes.Select((type, i) =>
            $"{{\"id\":\"{Guid.NewGuid()}\",\"seq\":{i + 1},\"type\":\"System\",\"systemEvent\":{{\"type\":\"{type}\"}}}}");
        return $"{{\"messages\":[{string.Join(",", messages)}],\"hasMoreBefore\":true}}";
    }

    [Fact]
    public void A_service_message_type_from_a_newer_server_reads_as_unknown_and_the_rest_of_the_page_still_loads()
    {
        var page = JsonSerializer.Deserialize<MessagesPageDto>(Page("TitleChanged", "SomethingAddedNextYear", "contentProtectionEnabled"), SkyJson.Options)!;

        page.Messages.Select(m => m.SystemEvent!.Type).ShouldBe(
            [SystemEventType.TitleChanged, SystemEventType.Unknown, SystemEventType.ContentProtectionEnabled]);
        page.HasMoreBefore.ShouldBeTrue();
    }

    [Fact]
    public void Known_types_round_trip_as_names_and_numbers_still_read()
    {
        var json = JsonSerializer.Serialize(new SystemEventDto { Type = SystemEventType.ContentProtectionDisabled }, SkyJson.Options);
        json.ShouldContain("\"type\":\"ContentProtectionDisabled\"");
        JsonSerializer.Deserialize<SystemEventDto>(json, SkyJson.Options)!.Type.ShouldBe(SystemEventType.ContentProtectionDisabled);

        // A cached Unknown stays Unknown.
        var unknown = JsonSerializer.Serialize(new SystemEventDto { Type = SystemEventType.Unknown }, SkyJson.Options);
        JsonSerializer.Deserialize<SystemEventDto>(unknown, SkyJson.Options)!.Type.ShouldBe(SystemEventType.Unknown);
        JsonSerializer.Deserialize<SystemEventDto>("""{"type":10}""", SkyJson.Options)!.Type.ShouldBe(SystemEventType.MessagePinned);
    }

    [Fact]
    public void Other_enums_stay_strict_and_configuring_twice_adds_nothing()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<ChatSummaryDto>("""{"type":"Channel"}""", SkyJson.Options));

        var options = SkyJson.Create();
        var count = options.Converters.Count;
        SkyJson.Configure(options);
        options.Converters.Count.ShouldBe(count);
    }
}
