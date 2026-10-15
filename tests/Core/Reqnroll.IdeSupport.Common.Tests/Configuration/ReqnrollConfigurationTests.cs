using Reqnroll.IdeSupport.Common.Configuration;
using Xunit;

namespace Reqnroll.IdeSupport.Common.Tests.Configuration;

public class ReqnrollConfigurationTests
{
    // ── CheckConfiguration - valid versions ───────────────────────────────────

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.0-rc1")]
    [InlineData("1.0.0-RC1")]
    [InlineData("1.0.0-Beta")]
    [InlineData("1.0.0-ALPHA")]
    [InlineData("1.0.0-RC-1")]
    public void CheckConfiguration_WhenVersionIsValid_DoesNotThrow(string version)
    {
        var config = new ReqnrollConfiguration { Version = version };

        config.CheckConfiguration();
    }

    [Fact]
    public void CheckConfiguration_WhenVersionIsNull_DoesNotThrow()
    {
        var config = new ReqnrollConfiguration { Version = null };

        config.CheckConfiguration();
    }

    // ── CheckConfiguration - invalid versions ─────────────────────────────────

    [Theory]
    [InlineData("abc")]
    [InlineData("1")]
    [InlineData("1.0.0-?")]
    public void CheckConfiguration_WhenVersionIsInvalid_Throws(string version)
    {
        var config = new ReqnrollConfiguration { Version = version };

        Assert.Throws<IdeSupportConfigurationException>(() => config.CheckConfiguration());
    }

    [Fact]
    public void CheckConfiguration_WhenVersionHasTrailingNewline_Throws()
    {
        var config = new ReqnrollConfiguration { Version = "1.0.0\n" };

        Assert.Throws<IdeSupportConfigurationException>(() => config.CheckConfiguration());
    }
}