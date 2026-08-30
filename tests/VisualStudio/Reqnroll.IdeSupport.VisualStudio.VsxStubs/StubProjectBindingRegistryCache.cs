#if false // Deferred: IProjectBindingRegistryCache, ProjectBindingRegistry not yet ported
#nullable enable
namespace Reqnroll.VisualStudio.VsxStubs;

public class StubProjectBindingRegistryCache
{
    private readonly IProjectBindingRegistryCache _substitute;

    public StubProjectBindingRegistryCache()
    {
        _substitute = Substitute.For<IProjectBindingRegistryCache>();

        Value = ProjectBindingRegistry.Invalid;
        _substitute.Update(Arg.Any<Func<ProjectBindingRegistry, Task<ProjectBindingRegistry>>>())
                   .Returns(async callInfo =>
                   {
                       var updateFunc = callInfo.Arg<Func<ProjectBindingRegistry, Task<ProjectBindingRegistry>>>();
                       Value = await updateFunc(Value);
                   });
    }

    public event EventHandler<EventArgs>? Changed;

    public Task Update(Func<ProjectBindingRegistry, ProjectBindingRegistry> updateFunc)
        => Update(registry => Task.FromResult(updateFunc(registry)));

    public Task Update(Func<ProjectBindingRegistry, Task<ProjectBindingRegistry>> updateTask)
        => _substitute.Update(updateTask);

    public ProjectBindingRegistry Value { get; private set; }
    public Task<ProjectBindingRegistry> GetLatest() => throw new NotImplementedException();
}

#endif
