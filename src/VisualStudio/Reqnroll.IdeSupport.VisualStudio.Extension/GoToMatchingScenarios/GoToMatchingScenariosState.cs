#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToMatchingScenarios;

/// <summary>
/// Container-registered singleton holder for the runtime-created "Go to Matching Scenarios"
/// service (issue #373) — the inverse of <see cref="GoToHooks.GoToHooksState"/>.
/// </summary>
/// <remarks>
/// <see cref="GoToMatchingScenariosService"/> depends on <c>LspInterceptingPipe</c>, which only
/// exists after the language server connection is established — too late for plain DI
/// construction. <see cref="ReqnrollLanguageClient"/> populates this on server init and clears it
/// on dispose; <see cref="HookMatchCountCodeLens.HookMatchCountCodeLensProvider"/>'s lens reads it.
/// </remarks>
internal sealed class GoToMatchingScenariosState
{
    /// <summary>Set once the server has initialised; null before that and after dispose.</summary>
    public GoToMatchingScenariosService? Service { get; set; }
}
