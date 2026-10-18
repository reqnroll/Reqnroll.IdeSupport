using System;
using System.Collections.Generic;
using AwesomeAssertions;
using EnvDTE;
using NSubstitute;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.VisualStudio.IdeServices;
using Xunit;
using Project = EnvDTE.Project;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.ProjectSystem;

/// <summary>
/// Issue #1030: <see cref="VsIdeScope"/> caches one <see cref="VsProjectScope"/> per project (keyed by
/// the project file path) and used to keep it forever. After a project was unloaded/removed and
/// reloaded under the same path, the stale scope - still holding the old, dead DTE
/// <see cref="Project"/> and its cached settings - kept being served, and neither eviction nor
/// <see cref="VsIdeScope.Dispose"/> ever disposed a scope's <see cref="IProjectScope.Properties"/>.
/// </summary>
[Collection(TestUiThreadCollection.Name)]
public sealed class VsIdeScopeProjectScopeLifecycleTests : IDisposable
{
    private const string ProjectFile = @"C:\Solution\MyProject\MyProject.csproj";
    private const string OtherProjectFile = @"C:\Solution\Other\Other.csproj";

    private readonly List<IProjectScope> _createdScopes = new();

    // Belt and braces: a scope's ProjectSettingsProvider may own a retry timer, so make sure nothing
    // created here outlives the test even when an assertion (or the unfixed code) leaked it.
    public void Dispose()
    {
        foreach (var scope in _createdScopes)
            scope.Dispose();
    }

    private static VsIdeScope CreateSut()
    {
        var dte = Substitute.For<DTE>();
        dte.Solution.IsOpen.Returns(true);
        var serviceProvider = Substitute.For<IServiceProvider>();
#pragma warning disable VSSDK006 // NSubstitute stub configuration, not a real service lookup
        serviceProvider.GetService(typeof(DTE)).Returns(dte);
#pragma warning restore VSSDK006

        return new VsIdeScope(serviceProvider,
            Substitute.For<ITelemetryService>(),
            Substitute.For<IFileSystemForIDE>(),
            Substitute.For<IIdeSupportLogger>());
    }

    private static Project CreateProject(string fullName = ProjectFile)
    {
        var project = Substitute.For<Project>();
        project.FullName.Returns(fullName);
        project.Name.Returns(System.IO.Path.GetFileNameWithoutExtension(fullName));
        // A real VS project always reports one; project-settings initialisation dereferences it.
        project.Properties.Item("TargetFrameworkMoniker").Value.Returns(".NETCoreApp,Version=v8.0");
        return project;
    }

    private IProjectScope GetProjectScope(VsIdeScope sut, Project project)
    {
        var scope = sut.GetProjectScope(project);
        _createdScopes.Add(scope);
        return scope;
    }

    private static IDisposable AttachDisposableProperty(IProjectScope scope)
    {
        var disposable = Substitute.For<IDisposable>();
        scope.Properties[typeof(VsIdeScopeProjectScopeLifecycleTests)] = disposable;
        return disposable;
    }

    [Fact]
    public void The_same_scope_is_served_while_the_project_stays_loaded() => TestUiThread.Run(() =>
    {
        var sut = CreateSut();
        var project = CreateProject();

        var first = GetProjectScope(sut, project);
        var second = GetProjectScope(sut, project);

        first.Should().BeOfType<VsProjectScope>();
        second.Should().BeSameAs(first);
    });

    [Fact]
    public void After_the_project_is_removed_a_reloaded_project_gets_a_fresh_scope() => TestUiThread.Run(() =>
    {
        var sut = CreateSut();
        var unloadedProject = CreateProject();
        var staleScope = GetProjectScope(sut, unloadedProject);

        sut.RemoveProjectScope(unloadedProject);

        // VS hands out a new DTE Project for the reloaded project, under the same file path.
        var reloadedScope = GetProjectScope(sut, CreateProject());
        reloadedScope.Should().NotBeSameAs(staleScope);
    });

    [Fact]
    public void Removing_a_project_disposes_its_scope_exactly_once() => TestUiThread.Run(() =>
    {
        var sut = CreateSut();
        var project = CreateProject();
        var scope = GetProjectScope(sut, project);
        var property = AttachDisposableProperty(scope);

        sut.RemoveProjectScope(project);
        sut.RemoveProjectScope(project);

        property.Received(1).Dispose();
    });

    [Fact]
    public void Removing_one_project_leaves_other_projects_scopes_alone() => TestUiThread.Run(() =>
    {
        var sut = CreateSut();
        var removedProject = CreateProject();
        var otherProject = CreateProject(OtherProjectFile);
        GetProjectScope(sut, removedProject);
        var otherScope = GetProjectScope(sut, otherProject);
        var otherProperty = AttachDisposableProperty(otherScope);

        sut.RemoveProjectScope(removedProject);

        otherProperty.DidNotReceive().Dispose();
        GetProjectScope(sut, otherProject).Should().BeSameAs(otherScope);
    });

    [Fact]
    public void Removing_all_project_scopes_disposes_and_forgets_each_of_them() => TestUiThread.Run(() =>
    {
        var sut = CreateSut();
        var project = CreateProject();
        var otherProject = CreateProject(OtherProjectFile);
        var scope = GetProjectScope(sut, project);
        var otherScope = GetProjectScope(sut, otherProject);
        var property = AttachDisposableProperty(scope);
        var otherProperty = AttachDisposableProperty(otherScope);

        sut.RemoveAllProjectScopes();

        property.Received(1).Dispose();
        otherProperty.Received(1).Dispose();
        GetProjectScope(sut, project).Should().NotBeSameAs(scope);
    });

    [Fact]
    public void Disposing_the_ide_scope_disposes_the_project_scopes_it_created() => TestUiThread.Run(() =>
    {
        var sut = CreateSut();
        var property = AttachDisposableProperty(GetProjectScope(sut, CreateProject()));
        var otherProperty = AttachDisposableProperty(GetProjectScope(sut, CreateProject(OtherProjectFile)));

        sut.Dispose();

        property.Received(1).Dispose();
        otherProperty.Received(1).Dispose();
    });
}
