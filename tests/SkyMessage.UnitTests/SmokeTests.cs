using Shouldly;
using SkyMessage.Domain.Entities;
using Xunit;

namespace SkyMessage.UnitTests;

public sealed class SmokeTests
{
    [Fact]
    public void PrivateKey_is_order_independent()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        Chat.PrivateKeyFor(a, b).ShouldBe(Chat.PrivateKeyFor(b, a));
    }
}
