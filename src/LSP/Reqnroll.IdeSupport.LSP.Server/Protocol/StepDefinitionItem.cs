#nullable enable

using Newtonsoft.Json;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Server.Protocol;

/// <summary>
/// One step-definition binding, as reported by <c>reqnroll/findUnusedStepDefinitions</c> (a binding
/// with zero matching steps across the workspace) and <c>reqnroll/goToStepDefinition</c> (a binding
/// matching the step at the caret, issue #757).
/// </summary>
public sealed class StepDefinitionItem
{
    /// <summary>Short project name that owns this step-definition binding.</summary>
    [JsonProperty("projectName")]
    public string? ProjectName { get; set; }

    /// <summary>Declaring class name (last segment of the qualified type name).</summary>
    [JsonProperty("className")]
    public string? ClassName { get; set; }

    /// <summary>Method name (without parameters or return type).</summary>
    [JsonProperty("methodName")]
    public string? MethodName { get; set; }

    /// <summary>The binding expression as written in the step attribute, e.g. <c>"the sum is {int}"</c>.</summary>
    [JsonProperty("bindingExpression")]
    public string? BindingExpression { get; set; }

    /// <summary>
    /// Absolute path of the C# source file containing this binding, or <see langword="null"/> when
    /// no file on this machine corresponds to it — see <see cref="IsResolved"/>. Clients must keep
    /// treating an absent value as "not navigable" rather than assuming a path is always present.
    /// </summary>
    [JsonProperty("sourceFile")]
    public string? SourceFile { get; set; }

    /// <summary>
    /// Whether <see cref="SourceFile"/> names a file that exists on this machine. Always
    /// <see langword="true"/> for a locally built project.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> means the assembly was built elsewhere — a container, a CI agent,
    /// another machine, or an external binding package — and the source path it recorded could not
    /// be mapped onto anything here (issue #540). The entry is still reported, because "this step
    /// definition is unused" remains true and useful; what changes is that a client must not offer
    /// to navigate to it, and should say why instead of appearing to ignore the click.
    /// </remarks>
    [JsonProperty("isResolved")]
    public bool IsResolved { get; set; } = true;

    /// <summary>
    /// The source path exactly as binding discovery recorded it, when that differs from
    /// <see cref="SourceFile"/>; otherwise <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// This is the only field that can explain an <see cref="IsResolved"/> of
    /// <see langword="false"/> to a user: "recorded at /workspaces/host-solution/Support/Hooks.cs"
    /// tells them the assembly came from a container build, where "nothing happened" tells them
    /// nothing. Also populated when a foreign path <em>was</em> successfully remapped, so the
    /// provenance stays visible.
    /// </remarks>
    [JsonProperty("recordedSourceFile")]
    public string? RecordedSourceFile { get; set; }

    /// <summary>
    /// The binding's step keyword — <c>"Given"</c>, <c>"When"</c> or <c>"Then"</c> — so a client can
    /// show the attribute (e.g. <c>[Given("the sum is {int}")]</c>); <see langword="null"/> when
    /// unknown. A <c>[StepDefinition]</c> attribute is registered as one binding per keyword, so it
    /// reports whichever of the three this binding is (issue #757).
    /// </summary>
    [JsonProperty("stepDefinitionType")]
    public string? StepDefinitionType { get; set; }

    /// <summary>Maps a binding's <see cref="ScenarioBlock"/> to the <see cref="StepDefinitionType"/> wire value.</summary>
    internal static string? ToWireType(ScenarioBlock block) =>
        block == ScenarioBlock.Unknown ? null : block.ToString();

    /// <summary>0-based line of the binding method in <see cref="SourceFile"/>.</summary>
    [JsonProperty("sourceLine")]
    public int SourceLine { get; set; }

    /// <summary>0-based column of the binding method in <see cref="SourceFile"/>.</summary>
    [JsonProperty("sourceChar")]
    public int SourceChar { get; set; }
}
