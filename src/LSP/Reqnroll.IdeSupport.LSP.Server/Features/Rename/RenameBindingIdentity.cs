using Reqnroll.IdeSupport.LSP.Core.Bindings;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>
/// Builds a content-addressed key identifying <i>which</i> binding a
/// <c>reqnroll/selectRenameTarget</c> disambiguation picked, so the subsequent
/// <c>textDocument/rename</c> can find that same binding again rather than trusting a positional
/// index (issue #671, R5).
/// </summary>
/// <remarks>
/// <para>
/// The session used to remember only the picker's <c>attributeIndex</c>, and
/// <see cref="RenameBindingResolver.ResolveBindingForRename"/> re-derived the candidate list at
/// rename time and indexed into it. The list is therefore recomputed against current state while
/// the index was chosen against the old one: if the user edits the <c>.cs</c> file while the
/// "Enter the new step expression" dialog is open — adding an attribute, reordering two — index
/// <c>N</c> now denotes a different binding, and the server renames the wrong step while producing
/// an otherwise perfectly valid, version-consistent edit. No document-version check catches that,
/// because every document involved really is at the version the edit was computed against.
/// </para>
/// <para>
/// The candidate list is not even guaranteed stable in the absence of edits:
/// <see cref="RenameBindingResolver.FindBindingsAtFeatureStep"/> accumulates into a
/// <c>HashSet</c> and returns <c>ToList()</c>, so its order depends on the insertion sequence the
/// match cache happens to produce, which a reparse between the two calls can change.
/// </para>
/// <para>
/// <b>What makes a binding identifiable</b> differs by where the rename was invoked, so the key
/// carries enough for both. From a <c>.feature</c> step, the competing candidates are typically
/// <i>the same step text bound to different methods</i> — that is what made them ambiguous — so
/// <see cref="StepDefinitionImplementation.Method"/> is the discriminator and the expression is
/// not. From a <c>.cs</c> attribute, every candidate is an attribute on one method, so the method
/// is identical and the step type, expression and scope discriminate instead.
/// </para>
/// <para>
/// Deliberately built from registry-projection values (<see cref="ProjectStepDefinitionBinding.DisplayExpression"/>,
/// not the live source literal): both ends of the comparison read the same registry, and resolving
/// the source literal costs a Roslyn round trip that the rename path would otherwise not pay
/// before it has even chosen a binding.
/// </para>
/// </remarks>
internal static class RenameBindingIdentity
{
    /// <summary>The identity key for <paramref name="binding"/>.</summary>
    public static string For(ProjectStepDefinitionBinding binding) =>
        string.Join("|",
            binding.StepDefinitionType,
            binding.Implementation?.Method ?? "",
            binding.DisplayExpression ?? "",
            binding.Scope?.Tag?.ToString() ?? "");

    /// <summary>
    /// The single binding in <paramref name="candidates"/> matching <paramref name="identity"/>,
    /// or <see langword="null"/> when the picked binding is no longer among them — the signal that
    /// the pending session went stale and the rename must not fall back to guessing.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> rather than the first of several when the key is ambiguous
    /// (two genuinely indistinguishable bindings on one method, e.g. <c>[Given("x")]</c> twice):
    /// there is nothing to choose between them, and renaming an arbitrary one is the failure this
    /// exists to prevent.
    /// </remarks>
    public static ProjectStepDefinitionBinding? FindIn(
        IReadOnlyList<ProjectStepDefinitionBinding> candidates, string identity)
    {
        ProjectStepDefinitionBinding? found = null;

        foreach (var candidate in candidates)
        {
            if (!string.Equals(For(candidate), identity, StringComparison.Ordinal))
                continue;

            if (found != null)
                return null;

            found = candidate;
        }

        return found;
    }
}
