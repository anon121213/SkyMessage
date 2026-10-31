using Shouldly;
using SkyMessage.Application.Calls;
using Xunit;

namespace SkyMessage.UnitTests.Calls;

public sealed class LiveKitNamesTests
{
    [Fact]
    public void Identity_is_user_and_session_without_dashes_and_round_trips()
    {
        var userId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();

        var identity = LiveKitNames.Identity(userId, sessionId);

        identity.ShouldBe($"{userId:N}.{sessionId:N}");
        LiveKitNames.TryParseIdentity(identity, out var parsedUser, out var parsedSession).ShouldBeTrue();
        parsedUser.ShouldBe(userId);
        parsedSession.ShouldBe(sessionId);
        LiveKitNames.BelongsTo(identity, userId).ShouldBeTrue();
        LiveKitNames.BelongsTo(identity, sessionId).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("egress-recorder")]
    [InlineData("01a1893af988737e9b60125c3483e521")]
    [InlineData("01a1893af988737e9b60125c3483e521-01a1893af98876bfa26b10505aa47db2")]
    [InlineData("01a1893a-f988-737e-9b60-125c3483e521.01a1893af98876bfa26b10505aa47db2")]
    [InlineData("zz a1893af988737e9b60125c3483e521.01a1893af98876bfa26b10505aa47db2")]
    public void Foreign_identities_are_not_parsed(string? identity)
    {
        LiveKitNames.TryParseIdentity(identity, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Room_name_round_trips()
    {
        var callId = Guid.CreateVersion7();

        var room = LiveKitNames.RoomName(callId);

        room.ShouldBe($"call-{callId:N}");
        LiveKitNames.TryParseRoomName(room, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(callId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("call-probe")]
    [InlineData("room-01a1893af988737e9b60125c3483e521")]
    [InlineData("call-01a1893a-f988-737e-9b60-125c3483e521")]
    public void Foreign_rooms_are_not_parsed(string? room)
    {
        LiveKitNames.TryParseRoomName(room, out _).ShouldBeFalse();
    }
}
