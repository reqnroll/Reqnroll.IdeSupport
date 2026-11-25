#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio.LineCodeLens;

/// <summary>
/// The lens kinds a <see cref="LineElementDescription"/> can list — one per classic CodeLens
/// data-point provider. Must not contain <c>|</c> or <c>,</c>.
/// </summary>
internal static class LineLensKinds
{
    /// <summary>The Run lens on Feature/Rule/Scenario lines.</summary>
    public const string Run = "run";

    /// <summary>The own-level hook-match-count lens.</summary>
    public const string Hooks = "hooks";

    /// <summary>The step-hooks lens that a Scenario: line can carry next to the own-level one.</summary>
    public const string StepHooks = "step-hooks";
}
