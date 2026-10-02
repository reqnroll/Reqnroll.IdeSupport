using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.TestOutcomes;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.TestOutcomes;

/// <summary>
/// Issue #714: <c>REQNROLL_TEST_OUTCOMES_PATH</c> is the seam the performance benchmarks use to keep the
/// outcome store off the developer's real 30-day history. It must win when set and be inert otherwise.
/// </summary>
[Collection("REQNROLL_TEST_OUTCOMES_PATH environment variable")]
public class TestOutcomePersistencePathTests : IDisposable
{
    private readonly string? _original = Environment.GetEnvironmentVariable(TestOutcomePersistence.FilePathEnvironmentVariable);
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    public void Dispose() =>
        Environment.SetEnvironmentVariable(TestOutcomePersistence.FilePathEnvironmentVariable, _original);

    [Fact]
    public void Environment_variable_overrides_the_resolved_path()
    {
        var path = Path.Combine(Path.GetTempPath(), "reqnroll-outcomes-override", "test-outcomes.json");
        Environment.SetEnvironmentVariable(TestOutcomePersistence.FilePathEnvironmentVariable, path);

        TestOutcomePersistence.ResolveFilePath(_logger).Should().Be(path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_or_blank_variable_falls_back_to_the_default_path(string? value)
    {
        Environment.SetEnvironmentVariable(TestOutcomePersistence.FilePathEnvironmentVariable, value);

        var resolved = TestOutcomePersistence.ResolveFilePath(_logger);

        resolved.Should().EndWith("test-outcomes.json");
        resolved.Should().NotBeNullOrWhiteSpace();
    }
}
