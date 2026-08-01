using Reqnroll.Bindings.Provider.Data;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using ReqnrollConnector.Discovery;
using ReqnrollConnector.Logging;

namespace Reqnroll.IdeSupport.LSP.Connector.Tests.Discovery;

/// <summary>
/// Covers <see cref="DiscoveryResultTransformer"/>'s process-boundary transform from the
/// connector's internal <see cref="BindingData"/> discovery shape to the wire-level
/// <see cref="InternalDiscoveryResult"/>/<see cref="StepDefinition"/>/<see cref="Hook"/> models.
/// </summary>
public class DiscoveryResultTransformerTests
{
    private readonly ISourceLocationProvider _sourceLocationProvider = Substitute.For<ISourceLocationProvider>();
    private readonly ITelemetryContainer _telemetry = Substitute.For<ITelemetryContainer>();
    private readonly DiscoveryResultTransformer _sut = new();

    private static BindingSourceMethodData Method(string type = "MyApp.Steps", string fullName = "Void MyStep()") =>
        new() { Type = type, FullName = fullName };

    private static BindingData EmptyBindingData() => new()
    {
        StepDefinitions = Array.Empty<StepDefinitionData>(),
        Hooks = Array.Empty<HookData>(),
        Errors = Array.Empty<string>(),
        Warnings = Array.Empty<string>(),
        StepArgumentTransformations = Array.Empty<StepArgumentTransformationData>()
    };

    // ── Step definitions ────────────────────────────────────────────────────────

    [Fact]
    public void Transform_maps_a_step_definition_s_basic_fields()
    {
        var bindingData = EmptyBindingData();
        bindingData.StepDefinitions = new[]
        {
            new StepDefinitionData
            {
                Type = "Given",
                Regex = "^my step$",
                Expression = "my step",
                Source = new BindingSourceData { Method = Method(), SourceLocation = "Steps.cs|5|1|5|10" },
                ParamTypes = Array.Empty<string>()
            }
        };

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.StepDefinitions.Should().ContainSingle();
        var stepDefinition = result.StepDefinitions[0];
        stepDefinition.Type.Should().Be("Given");
        stepDefinition.Regex.Should().Be("^my step$");
        stepDefinition.Expression.Should().Be("my step");
    }

    [Fact]
    public void Transform_keeps_a_null_Expression_as_null_rather_than_falling_back_to_Regex()
    {
        // A missing Expression signals a method-name-style binding with no explicit attribute
        // string; that signal must survive the wire so the server can derive the same fallback
        // display text itself, rather than being collapsed here.
        var bindingData = EmptyBindingData();
        bindingData.StepDefinitions = new[]
        {
            new StepDefinitionData
            {
                Type = "Given",
                Regex = "^my step$",
                Expression = null,
                Source = new BindingSourceData { Method = Method() },
                ParamTypes = Array.Empty<string>()
            }
        };

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.StepDefinitions[0].Expression.Should().BeNull();
    }

    [Fact]
    public void Transform_formats_the_method_reference_as_DeclaringType_dot_signature()
    {
        var bindingData = EmptyBindingData();
        bindingData.StepDefinitions = new[]
        {
            new StepDefinitionData
            {
                Type = "Given",
                Regex = "^my step$",
                Source = new BindingSourceData
                {
                    Method = new BindingSourceMethodData { Type = "MyApp.Namespace.Steps", FullName = "Void MyStep(System.String)" }
                },
                ParamTypes = Array.Empty<string>()
            }
        };

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.StepDefinitions[0].Method.Should().Be("Steps.MyStep(System.String)");
    }

    [Fact]
    public void Transform_returns_placeholder_method_reference_when_method_is_null()
    {
        var bindingData = EmptyBindingData();
        bindingData.StepDefinitions = new[]
        {
            new StepDefinitionData { Type = "Given", Regex = "^my step$", Source = null, ParamTypes = Array.Empty<string>() }
        };

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.StepDefinitions[0].Method.Should().Be("???");
    }

    [Theory]
    [InlineData("s", "System.String")]
    [InlineData("i", "System.Int32")]
    public void Transform_encodes_known_param_types_using_their_shortcut(string shortcut, string fullTypeName)
    {
        var bindingData = EmptyBindingData();
        bindingData.StepDefinitions = new[]
        {
            new StepDefinitionData
            {
                Type = "Given", Regex = "^my step$", Source = new BindingSourceData { Method = Method() },
                ParamTypes = new[] { fullTypeName }
            }
        };

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.StepDefinitions[0].ParamTypes.Should().Be(shortcut);
    }

    [Fact]
    public void Transform_encodes_an_unknown_param_type_via_the_type_name_dictionary_and_reverses_it()
    {
        var bindingData = EmptyBindingData();
        bindingData.StepDefinitions = new[]
        {
            new StepDefinitionData
            {
                Type = "Given", Regex = "^my step$", Source = new BindingSourceData { Method = Method() },
                ParamTypes = new[] { "MyApp.CustomType" }
            }
        };

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.StepDefinitions[0].ParamTypes.Should().Be("#0");
        result.TypeNames.Should().ContainKey("0").WhoseValue.Should().Be("MyApp.CustomType");
    }

    [Fact]
    public void Transform_joins_multiple_param_types_with_pipe()
    {
        var bindingData = EmptyBindingData();
        bindingData.StepDefinitions = new[]
        {
            new StepDefinitionData
            {
                Type = "Given", Regex = "^my step$", Source = new BindingSourceData { Method = Method() },
                ParamTypes = new[] { "System.String", "System.Int32" }
            }
        };

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.StepDefinitions[0].ParamTypes.Should().Be("s|i");
    }

    [Fact]
    public void Transform_returns_null_param_types_when_there_are_none()
    {
        var bindingData = EmptyBindingData();
        bindingData.StepDefinitions = new[]
        {
            new StepDefinitionData
            {
                Type = "Given", Regex = "^my step$", Source = new BindingSourceData { Method = Method() },
                ParamTypes = Array.Empty<string>()
            }
        };

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.StepDefinitions[0].ParamTypes.Should().BeNull();
    }

    [Fact]
    public void Transform_maps_scope_fields_when_present()
    {
        var bindingData = EmptyBindingData();
        bindingData.StepDefinitions = new[]
        {
            new StepDefinitionData
            {
                Type = "Given", Regex = "^my step$", Source = new BindingSourceData { Method = Method() },
                ParamTypes = Array.Empty<string>(),
                Scope = new BindingScopeData { Tag = "@mytag", FeatureTitle = "F", ScenarioTitle = "S" }
            }
        };

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.StepDefinitions[0].Scope.Should().NotBeNull();
        result.StepDefinitions[0].Scope!.Tag.Should().Be("@mytag");
        result.StepDefinitions[0].Scope!.FeatureTitle.Should().Be("F");
        result.StepDefinitions[0].Scope!.ScenarioTitle.Should().Be("S");
    }

    [Fact]
    public void Transform_leaves_scope_null_when_absent()
    {
        var bindingData = EmptyBindingData();
        bindingData.StepDefinitions = new[]
        {
            new StepDefinitionData
            {
                Type = "Given", Regex = "^my step$", Source = new BindingSourceData { Method = Method() },
                ParamTypes = Array.Empty<string>(), Scope = null
            }
        };

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.StepDefinitions[0].Scope.Should().BeNull();
    }

    [Fact]
    public void Transform_uses_the_source_data_s_own_SourceLocation_when_already_set()
    {
        var bindingData = EmptyBindingData();
        bindingData.StepDefinitions = new[]
        {
            new StepDefinitionData
            {
                Type = "Given", Regex = "^my step$", ParamTypes = Array.Empty<string>(),
                Source = new BindingSourceData { Method = Method(), SourceLocation = "already-resolved" }
            }
        };

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.StepDefinitions[0].SourceLocation.Should().Be("already-resolved");
        _sourceLocationProvider.DidNotReceiveWithAnyArgs().GetSourceLocation(default!);
    }

    [Fact]
    public void Transform_falls_back_to_the_source_location_provider_when_not_already_resolved()
    {
        var bindingData = EmptyBindingData();
        var method = Method();
        bindingData.StepDefinitions = new[]
        {
            new StepDefinitionData
            {
                Type = "Given", Regex = "^my step$", ParamTypes = Array.Empty<string>(),
                Source = new BindingSourceData { Method = method, SourceLocation = null }
            }
        };
        _sourceLocationProvider.GetSourceLocation(method)
            .Returns(new SourceLocation("Steps.cs", 5, 1, 5, 10));

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.StepDefinitions[0].SourceLocation.Should().Be("#0|5|1|5|10");
        result.SourceFiles.Should().ContainKey("0").WhoseValue.Should().Be("Steps.cs");
    }

    [Fact]
    public void Transform_leaves_SourceLocation_null_when_source_data_is_null()
    {
        var bindingData = EmptyBindingData();
        bindingData.StepDefinitions = new[]
        {
            new StepDefinitionData { Type = "Given", Regex = "^my step$", ParamTypes = Array.Empty<string>(), Source = null }
        };

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.StepDefinitions[0].SourceLocation.Should().BeNull();
    }

    [Fact]
    public void Transform_orders_step_definitions_by_SourceLocation()
    {
        var bindingData = EmptyBindingData();
        bindingData.StepDefinitions = new[]
        {
            new StepDefinitionData { Type = "Given", Regex = "^b$", Source = new BindingSourceData { Method = Method(), SourceLocation = "b-location" }, ParamTypes = Array.Empty<string>() },
            new StepDefinitionData { Type = "Given", Regex = "^a$", Source = new BindingSourceData { Method = Method(), SourceLocation = "a-location" }, ParamTypes = Array.Empty<string>() }
        };

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.StepDefinitions.Select(sd => sd.SourceLocation).Should().Equal("a-location", "b-location");
    }

    // ── Hooks ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Transform_maps_a_hook_s_fields()
    {
        var bindingData = EmptyBindingData();
        bindingData.Hooks = new[]
        {
            new HookData
            {
                Type = "BeforeScenario",
                HookOrder = 10,
                Source = new BindingSourceData { Method = Method(), SourceLocation = "Hooks.cs|1|1|1|5" },
                Scope = new BindingScopeData { Tag = "@init" }
            }
        };

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.Hooks.Should().ContainSingle();
        var hook = result.Hooks[0];
        hook.Type.Should().Be("BeforeScenario");
        hook.HookOrder.Should().Be(10);
        hook.Scope!.Tag.Should().Be("@init");
        hook.SourceLocation.Should().Be("Hooks.cs|1|1|1|5");
    }

    [Fact]
    public void Transform_orders_hooks_by_SourceLocation()
    {
        var bindingData = EmptyBindingData();
        bindingData.Hooks = new[]
        {
            new HookData { Type = "BeforeScenario", Source = new BindingSourceData { Method = Method(), SourceLocation = "z-location" } },
            new HookData { Type = "AfterScenario", Source = new BindingSourceData { Method = Method(), SourceLocation = "a-location" } }
        };

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.Hooks.Select(h => h.SourceLocation).Should().Equal("a-location", "z-location");
    }

    // ── Errors ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Transform_splits_errors_by_TypeLoadError_and_BindingError_prefix()
    {
        var bindingData = EmptyBindingData();
        bindingData.Errors = new[]
        {
            "TypeLoadError: could not load MyType",
            "BindingError: ambiguous binding",
            "TypeLoadError: could not load OtherType"
        };

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.TypeLoadErrors.Should().BeEquivalentTo(new[] { "could not load MyType", "could not load OtherType" });
        result.GenericBindingErrors.Should().BeEquivalentTo(new[] { "ambiguous binding" });
    }

    [Fact]
    public void Transform_returns_empty_error_arrays_when_Errors_is_null()
    {
        var bindingData = EmptyBindingData();
        bindingData.Errors = null;

        var result = _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        result.TypeLoadErrors.Should().BeEmpty();
        result.GenericBindingErrors.Should().BeEmpty();
    }

    // ── Telemetry ────────────────────────────────────────────────────────────────

    [Fact]
    public void Transform_reports_counts_via_telemetry()
    {
        var bindingData = EmptyBindingData();
        bindingData.StepDefinitions = new[]
        {
            new StepDefinitionData { Type = "Given", Regex = "^a$", Source = new BindingSourceData { Method = Method() }, ParamTypes = Array.Empty<string>() }
        };
        bindingData.Hooks = new[]
        {
            new HookData { Type = "BeforeScenario", Source = new BindingSourceData { Method = Method() } }
        };

        _sut.Transform(bindingData, _sourceLocationProvider, _telemetry);

        _telemetry.Received(1).AddTelemetryProperty("StepDefinitions", "1");
        _telemetry.Received(1).AddTelemetryProperty("Hooks", "1");
    }
}
