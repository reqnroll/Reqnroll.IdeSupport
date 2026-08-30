namespace Reqnroll.IdeSupport.LSP.Core.Tests.Bindings;

public class NullBindingRegistryProviderTests
{
    [Fact]
    public void Current_is_always_the_invalid_registry()
    {
        var sut = new NullBindingRegistryProvider();
        sut.Current.Should().BeSameAs(ProjectBindingRegistry.Invalid);
    }

    [Fact]
    public void Subscribing_and_unsubscribing_the_change_event_is_a_no_op()
    {
        var sut = new NullBindingRegistryProvider();
        EventHandler<bool> handler = (_, _) => { };

        var act = () =>
        {
            sut.BindingRegistryChanged += handler;
            sut.BindingRegistryChanged -= handler;
        };

        act.Should().NotThrow();
    }
}
