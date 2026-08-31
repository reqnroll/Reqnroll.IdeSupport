#nullable enable

using System.Collections.Generic;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;

/// <summary>One unused step-definition binding, as parsed from the server response.</summary>
internal sealed class UnusedStepLocation
{
    /// <summary>Short project name derived from the owning .csproj file name.</summary>
    public string? ProjectName       { get; set; }
    /// <summary>Declaring class name of the step-definition method.</summary>
    public string? ClassName         { get; set; }
    /// <summary>Name of the step-definition method.</summary>
    public string? MethodName        { get; set; }
    /// <summary>The step-definition's binding expression/regex.</summary>
    public string? BindingExpression { get; set; }
    /// <summary>Absolute path to the source file declaring the step definition, or null when no
    /// such file exists on this machine — see <see cref="IsResolved"/>.</summary>
    public string? SourceFile        { get; set; }
    /// <summary>0-based source line of the step-definition declaration.</summary>
    public int     SourceLine        { get; set; }  // 0-based
    /// <summary>0-based source character of the step-definition declaration.</summary>
    public int     SourceChar        { get; set; }  // 0-based
    /// <summary>
    /// Whether <see cref="SourceFile"/> names a file that exists here. False when the assembly was
    /// built elsewhere and the source path it recorded could not be mapped onto this machine
    /// (issue #540); such a row is listed but not navigable. Defaults to true so a response from an
    /// older server, which omits the field, behaves exactly as before.
    /// </summary>
    public bool    IsResolved        { get; set; } = true;
    /// <summary>The path the compiled assembly records, when it differs from <see cref="SourceFile"/>.</summary>
    public string? RecordedSourceFile { get; set; }
}

/// <summary>Parsed result from a <c>reqnroll/findUnusedStepDefinitions</c> response.</summary>
internal sealed class UnusedStepDefinitionsResult
{
    /// <summary>Sentinel for a workspace with no unused step definitions.</summary>
    public static readonly UnusedStepDefinitionsResult Empty =
        new(Array.Empty<UnusedStepLocation>());

    /// <summary>The unused step-definition locations.</summary>
    public IReadOnlyList<UnusedStepLocation> Items { get; }

    /// <summary>Creates a result wrapping the given unused step-definition locations.</summary>
    public UnusedStepDefinitionsResult(IReadOnlyList<UnusedStepLocation> items)
        => Items = items;
}
