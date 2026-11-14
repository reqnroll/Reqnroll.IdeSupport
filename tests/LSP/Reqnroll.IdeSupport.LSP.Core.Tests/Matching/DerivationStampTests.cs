using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Matching;

public class DerivationStampTests
{
    [Fact]
    public void Newer_text_wins_even_with_older_configuration_and_registry()
    {
        var newerText = new DerivationStamp(ContentGeneration: 6, ConfigGeneration: 1, RegistryVersion: 3);
        var olderText = new DerivationStamp(ContentGeneration: 5, ConfigGeneration: 9, RegistryVersion: 9);

        newerText.Should().BeGreaterThan(olderText);
        (newerText > olderText).Should().BeTrue();
        (olderText < newerText).Should().BeTrue();
    }

    [Fact]
    public void For_the_same_text_newer_configuration_wins_even_with_an_older_registry()
    {
        var newerConfig = new DerivationStamp(5, ConfigGeneration: 2, RegistryVersion: 3);
        var olderConfig = new DerivationStamp(5, ConfigGeneration: 1, RegistryVersion: 9);

        newerConfig.Should().BeGreaterThan(olderConfig);
    }

    [Fact]
    public void For_the_same_text_and_configuration_the_newer_registry_wins()
    {
        var newerRegistry = new DerivationStamp(5, 2, RegistryVersion: 8);
        var olderRegistry = new DerivationStamp(5, 2, RegistryVersion: 7);

        newerRegistry.Should().BeGreaterThan(olderRegistry);
    }

    [Fact]
    public void Stamps_of_the_same_inputs_are_equal_and_neither_is_newer()
    {
        var a = new DerivationStamp(5, 2, 7);
        var b = new DerivationStamp(5, 2, 7);

        a.Should().Be(b);
        a.CompareTo(b).Should().Be(0);
        (a >= b).Should().BeTrue();
        (a <= b).Should().BeTrue();
        (a > b).Should().BeFalse();
        (a < b).Should().BeFalse();
    }

    [Fact]
    public void Sorting_orders_stamps_from_oldest_to_newest_inputs()
    {
        var stamps = new[]
        {
            new DerivationStamp(2, 0, 1),
            new DerivationStamp(1, 5, 9),
            new DerivationStamp(2, 0, 0),
            new DerivationStamp(1, 6, 0),
        };

        stamps.Order().Should().Equal(
            new DerivationStamp(1, 5, 9),
            new DerivationStamp(1, 6, 0),
            new DerivationStamp(2, 0, 0),
            new DerivationStamp(2, 0, 1));
    }
}
