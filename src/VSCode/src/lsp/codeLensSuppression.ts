import { Middleware } from 'vscode-languageclient/node';

/**
 * Middleware that neutralizes `vscode-languageclient`'s own built-in generic CodeLens feature.
 *
 * Why this exists (issue #471 follow-up): the server now declares `codeLensProvider` (with
 * `resolveProvider: true`) statically in its initialize response, so that a capable client can
 * one day use the deferred-resolve path (see `ClientIdeContext.SupportsCodeLensResolve` on the
 * server). But this extension already has its own hand-rolled `vscode.CodeLensProvider`s --
 * `registerStepCodeLens` (`.cs`) and `registerHookCodeLens` (`.feature`, see `hookCodeLens.ts`) --
 * which call `client.sendRequest(CodeLensRequest.type, ...)` directly and have done so since
 * before the server ever advertised the capability.
 *
 * `vscode-languageclient`'s `LanguageClient` also ships a default `CodeLensFeature` that is
 * normally dormant while no `codeLensProvider` capability is declared, but activates
 * automatically -- registering *its own* `vscode.CodeLensProvider` against the same
 * `documentSelector` -- the moment the capability appears, regardless of the hand-rolled
 * providers already covering that ground. VS Code merges lenses from every provider registered
 * for a given line, so once that built-in feature woke up every hook-match and step-usage lens
 * started rendering twice (confirmed live: two near-simultaneous `textDocument/codeLens` requests
 * per file, each independently computing and returning the same lenses).
 *
 * Swallowing `provideCodeLenses` here (never calling `next`) keeps the built-in feature
 * registered but permanently empty, so the hand-rolled providers remain the sole source of
 * lenses -- exactly as before the server declared the capability. `resolveCodeLens` is included
 * for the same reason: with the built-in feature never producing an unresolved lens, VS Code
 * would never call it in practice, but leaving it wired up would silently resurrect the built-in
 * path if `provideCodeLenses` above ever changed to return real lenses.
 */
export function createCodeLensSuppressionMiddleware(): Middleware {
  return {
    provideCodeLenses: () => [],
    resolveCodeLens: () => undefined,
  };
}
