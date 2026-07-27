/**
 * Centralizes the custom `reqnroll/*` LSP method names used by this extension, mirroring
 * `LspMethodNames.cs` on the server (`src/LSP/Reqnroll.IdeSupport.LSP.Server/Protocol`).
 * Keep the two lists in sync when adding a new custom message.
 *
 * Standard LSP methods (`textDocument/codeLens`, `workspace/executeCommand`,
 * `telemetry/event`, etc.) don't need an entry here — use the typed request/notification
 * descriptors exported by `vscode-languageclient/node` instead (e.g. `CodeLensRequest.type`).
 */
export const ReqnrollMethods = {
  goToStepDefinitions: 'reqnroll/goToStepDefinitions',
  goToHooks: 'reqnroll/goToHooks',
  findStepUsages: 'reqnroll/findStepUsages',
  findUnusedStepDefinitions: 'reqnroll/findUnusedStepDefinitions',
  projectLoaded: 'reqnroll/projectLoaded',
  projectUnloaded: 'reqnroll/projectUnloaded',
  projectFiles: 'reqnroll/projectFiles',
  renameTargets: 'reqnroll/renameTargets',
  selectRenameTarget: 'reqnroll/selectRenameTarget',
} as const;
