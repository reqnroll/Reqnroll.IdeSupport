namespace Reqnroll.IdeSupport.Common.Tests.ProjectSystem.Configuration;

/// <summary>
/// Regression guard for <see cref="ProjectSystemDeveroomConfigurationProvider"/>'s known,
/// still-open no-op: solution-level configuration is never loaded, so
/// <see cref="ProjectSystemDeveroomConfigurationProvider.GetConfiguration"/> always returns a
/// fresh default <see cref="DeveroomConfiguration"/>, and
/// <see cref="ProjectSystemDeveroomConfigurationProvider.ConfigurationChanged"/> is never raised.
/// Once solution-level loading is implemented, these assertions should start failing, signalling
/// that this test needs to be updated to reflect the new behavior (issue #357).
/// </summary>
public class ProjectSystemDeveroomConfigurationProviderTests
{
    [Fact]
    public void GetConfiguration_returns_a_default_configuration_regardless_of_ide_scope()
    {
        var ideScope = Substitute.For<IIdeScope>();
        var provider = new ProjectSystemDeveroomConfigurationProvider(ideScope);

        var configuration = provider.GetConfiguration();

        configuration.ConfigurationBaseFolder.Should().BeNull();
        configuration.DefaultFeatureLanguage.Should().Be("en-US");
        configuration.ConfiguredBindingCulture.Should().BeNull();
        configuration.Reqnroll.ConfigFilePath.Should().BeNull();
        configuration.Reqnroll.IsReqnrollProject.Should().BeNull();
    }

    [Fact]
    public void GetConfiguration_returns_the_same_instance_on_repeated_calls()
    {
        var provider = new ProjectSystemDeveroomConfigurationProvider(Substitute.For<IIdeScope>());

        var first = provider.GetConfiguration();
        var second = provider.GetConfiguration();

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void ConfigurationChanged_is_never_raised()
    {
        var provider = new ProjectSystemDeveroomConfigurationProvider(Substitute.For<IIdeScope>());
        var raised = false;
        provider.ConfigurationChanged += (_, _) => raised = true;

        raised.Should().BeFalse();
    }
}
