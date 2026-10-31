using System.Text;
using Shouldly;
using SkyMessage.Application.Calls;
using Xunit;

namespace SkyMessage.UnitTests.Calls;

public sealed class LiveKitWebhookParsingTests
{
    [Fact]
    public void Participant_event_in_protojson_is_parsed()
    {
        var body = Encoding.UTF8.GetBytes(
            """{"event":"participant_left","room":{"sid":"RM_x","name":"call-01a1893af9887eab8c3a57ed8f3a2e6b","creationTime":"1793302344"},"participant":{"sid":"PA_y","identity":"01a1893af988737e9b60125c3483e521.01a1893af98876bfa26b10505aa47db2","state":"DISCONNECTED","joinedAt":"1793302345","disconnectReason":"CLIENT_INITIATED"},"id":"EV_z","createdAt":"1793302400"}""");

        CallWebhookService.TryParse(body, out var webhook).ShouldBeTrue();

        webhook.Event.ShouldBe("participant_left");
        webhook.Id.ShouldBe("EV_z");
        webhook.Room!.Name.ShouldBe("call-01a1893af9887eab8c3a57ed8f3a2e6b");
        webhook.Participant!.Identity.ShouldBe("01a1893af988737e9b60125c3483e521.01a1893af98876bfa26b10505aa47db2");
        webhook.Participant.Sid.ShouldBe("PA_y");
        webhook.Participant.IsDuplicateIdentity.ShouldBeFalse();
    }

    [Theory]
    [InlineData("\"DUPLICATE_IDENTITY\"", true)]
    [InlineData("2", true)]
    [InlineData("\"PARTICIPANT_REMOVED\"", false)]
    [InlineData("4", false)]
    public void Duplicate_identity_is_recognized_by_name_or_number(string reason, bool duplicate)
    {
        var body = Encoding.UTF8.GetBytes(
            """{"event":"participant_left","room":{"name":"call-x"},"participant":{"identity":"a","disconnectReason":""" + reason + "}}");

        CallWebhookService.TryParse(body, out var webhook).ShouldBeTrue();

        webhook.Participant!.IsDuplicateIdentity.ShouldBe(duplicate);
    }

    [Fact]
    public void Room_event_without_participant_is_parsed()
    {
        var body = Encoding.UTF8.GetBytes(
            """{"event":"room_finished","room":{"sid":"RM_nG8EuoKTERBg","name":"call-hooktest","emptyTimeout":30},"id":"EV_5jAJnfT6BuGV","createdAt":"1793302344","roomEndReason":"ROOM_END_API_DELETE"}""");

        CallWebhookService.TryParse(body, out var webhook).ShouldBeTrue();

        webhook.Event.ShouldBe("room_finished");
        webhook.Room!.Name.ShouldBe("call-hooktest");
        webhook.Participant.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    public void Garbage_is_not_parsed(string body)
    {
        CallWebhookService.TryParse(Encoding.UTF8.GetBytes(body), out _).ShouldBeFalse();
    }
}
