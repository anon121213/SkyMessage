using System.Linq.Expressions;
using Shouldly;
using SkyMessage.Application.Identity;
using Xunit;

namespace SkyMessage.UnitTests.Identity;

public sealed class IdentitySearchTests
{
    [Theory]
    [InlineData("anna", "%anna%")]
    [InlineData("100%", "%100\\%%")]
    [InlineData("a_b", "%a\\_b%")]
    [InlineData("back\\slash", "%back\\\\slash%")]
    [InlineData("Иван", "%Иван%")]
    public void Contains_pattern_escapes_like_wildcards(string term, string expected)
    {
        IdentitySearch.ContainsPattern(term).ShouldBe(expected);
    }

    [Fact]
    public void Or_else_combines_predicates_over_one_parameter()
    {
        Expression<Func<string, bool>> startsWithA = s => s.StartsWith('a');
        var combined = startsWithA.OrElse(s => s.EndsWith('z')).Compile();

        combined("abc").ShouldBeTrue();
        combined("xyz").ShouldBeTrue();
        combined("xyx").ShouldBeFalse();
    }
}
