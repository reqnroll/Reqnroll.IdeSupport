import * as vscode from 'vscode';
import {
  CancellationToken,
  LanguageClient,
  Middleware,
  PrepareRenameSignature,
  WorkspaceEdit as LspWorkspaceEdit,
} from 'vscode-languageclient/node';
import { ReqnrollMethods } from '../lsp/lspMethods';

/** One renameable binding attribute at the queried position (mirrors RenameTargetItem.cs). */
export interface RenameTargetItem {
  label: string;
  expression: string;
  attributeIndex: number;
}

interface RenameTargetsResponse {
  targets?: RenameTargetItem[];
}

/**
 * Queries `reqnroll/renameTargets` for the binding attribute(s) at `position`. Returns an empty
 * array when the server has nothing renameable there, or when the request itself fails (e.g. an
 * older server that doesn't implement it) — in either case the caller falls back to the normal
 * `prepareRename` flow.
 *
 * `requireAttributeLine` (issue #506): pass `true` for the F2-bound dispatcher, which must not
 * steal an ordinary C# rename-symbol away from a method or local identifier. Without it, the
 * server also matches a cursor anywhere on the bound method's declaration line (not just within
 * the binding attribute itself) — the correct, deliberately loose behavior for the explicit
 * "Reqnroll: Rename Step" context-menu/palette command, but wrong for F2, which must fall through
 * to `editor.action.rename` unless the cursor is genuinely on the binding expression.
 */
export async function getRenameTargets(
  client: LanguageClient,
  uriStr: string,
  position: vscode.Position,
  options?: { requireAttributeLine?: boolean },
): Promise<RenameTargetItem[]> {
  try {
    const response = await client.sendRequest<RenameTargetsResponse | null>(
      ReqnrollMethods.renameTargets,
      {
        textDocument: { uri: uriStr },
        position: { line: position.line, character: position.character },
        requireAttributeLine: options?.requireAttributeLine ?? false,
      },
    );
    return response?.targets ?? [];
  } catch {
    return [];
  }
}

/**
 * Shows a QuickPick listing the ambiguous binding attributes and returns the chosen item,
 * or `undefined` if the user dismissed the picker.
 */
export async function pickRenameTarget(
  targets: RenameTargetItem[],
): Promise<RenameTargetItem | undefined> {
  const items = targets.map((target) => ({
    label: target.label,
    description: target.expression,
    target,
  }));

  const picked = await vscode.window.showQuickPick(items, {
    placeHolder: 'Multiple step definitions match — choose which one to rename',
    matchOnDescription: true,
  });

  return picked?.target;
}

/**
 * Tells the server which attribute the user chose, so the subsequent `textDocument/rename`
 * resolves to that binding (see `RenameSessionManager`/`StepRenameHandler.HandleRenameAsync` —
 * the server keys the pending session by `(uri, version)` and always uses `version: 0` for this
 * flow, matching the Visual Studio client's `RenameStepService.SelectRenameTargetAsync`).
 */
export function selectRenameTarget(
  client: LanguageClient,
  uriStr: string,
  attributeIndex: number,
): Promise<void> {
  return client.sendNotification(ReqnrollMethods.selectRenameTarget, {
    uri: uriStr,
    version: 0,
    attributeIndex,
  });
}

/**
 * Collapses the active editor's text selection to a plain cursor before a `.feature` step rename
 * is invoked (issue #456). Must run BEFORE `editor.action.rename` starts — see the call site in
 * `extension.ts`'s `reqnroll.renameStep` command, not inside the `prepareRename` middleware below.
 *
 * For a `.feature`-triggered rename, `StepRenameHandler.HandlePrepareRenameAsync` seeds the
 * rename box with the binding's ABSTRACT Cucumber expression (e.g.
 * `"the client added {int} units of {string} to the basket"`), not the concrete text literally in
 * the buffer (`"the client added 1 units of \"Electric guitar\" to the basket"`) — deliberate, so
 * the box always yields an unambiguous new expression rather than requiring a fragile diff against
 * real parameter values. Those two strings only match character-for-character when every
 * parameter's rendered width happens to equal its abstract token's width.
 *
 * VS Code's built-in rename widget, when the user had an active selection at invocation time
 * (verified live), preserves it by re-applying that selection's raw character offset onto
 * whatever placeholder text comes back — correct for a `.cs` attribute rename, where the
 * placeholder IS the literal buffer text, but wrong here: the highlight lands wherever that offset
 * happens to fall inside the abstract expression, often mid-way through a `{parameter}` token
 * instead of on the word the user meant. An *unselected* cursor doesn't trigger this — verified
 * live, that case already selects the whole placeholder, the correct/intended default.
 *
 * Rather than changing what `Placeholder` contains (which would fix the highlight at the cost of
 * exposing raw parameter values in the common, already-correct no-selection case — see the design
 * discussion on issue #456), collapse the pre-existing selection to the cursor right before
 * `editor.action.rename` runs, so VS Code takes its own safe "no selection" path for a `.feature`
 * step specifically. A no-op for `.cs` files (and when there's no active selection), where the
 * preserved-selection behavior is already correct.
 *
 * This MUST happen before VS Code's rename command starts, not from inside `prepareRename`
 * middleware — verified live, mutating `editor.selection` while that command has an in-flight
 * `textDocument/prepareRename` request fires a selection-change event that VS Code treats as "the
 * user moved the cursor, cancel this rename," regardless of whether the mutation happens before or
 * after the request resolves. Simple (non-parameterized) steps can race past this — their
 * round-trip is fast enough that the command isn't listening for cancellation yet, or has already
 * finished — but parameterized steps, whose server-side matching takes longer, reliably lose the
 * race: the rename widget never opens at all. Collapsing the selection here, before
 * `editor.action.rename` is even invoked, avoids ever mutating the selection while that command is
 * active.
 */
export function collapseActiveSelectionForFeatureStepRename(): void {
  const editor = vscode.window.activeTextEditor;
  if (editor?.document.languageId !== 'gherkin' || editor.selection.isEmpty) return;

  editor.selection = new vscode.Selection(editor.selection.active, editor.selection.active);
}

/**
 * Builds a `RenameMiddleware.prepareRename` override that surfaces server-side rename ambiguity
 * (the Step Rename refactoring's multi-attribute case) with a VS Code–idiomatic `QuickPick`,
 * mirroring the disambiguation dialog the Visual Studio client shows via
 * `RenameStepCommand`/`RenameStepService`.
 *
 * When the server reports zero or exactly one candidate at the cursor, this delegates to `next`
 * unchanged (the standard `textDocument/prepareRename` flow). When it reports two or more, the
 * user picks one via QuickPick, `reqnroll/selectRenameTarget` records the choice for the next
 * `textDocument/rename` call, and `next` is still invoked so VS Code's native rename input box
 * opens as usual — only which binding gets renamed changes, not how rename is invoked.
 *
 * Returning `undefined` when the user dismisses the picker suppresses the rename, matching
 * `prepareRename` returning `null` elsewhere in this handler.
 *
 * `getClient` is a lazy accessor rather than a direct `LanguageClient` because this middleware
 * must be supplied to `LanguageClientOptions` before the `LanguageClient` itself is constructed
 * (see `extension.ts`); by the time VS Code actually invokes `prepareRename`, the client has
 * started and the accessor resolves.
 */
export function createRenameMiddleware(getClient: () => LanguageClient | undefined): Middleware {
  return {
    prepareRename: async (
      document: vscode.TextDocument,
      position: vscode.Position,
      token: CancellationToken,
      next: PrepareRenameSignature,
    ) => {
      const client = getClient();
      if (!client) return next(document, position, token);

      const targets = await getRenameTargets(client, document.uri.toString(), position);

      if (targets.length > 1) {
        const chosen = await pickRenameTarget(targets);
        if (!chosen) return undefined;

        await selectRenameTarget(client, document.uri.toString(), chosen.attributeIndex);
      }

      return next(document, position, token);
    },
  };
}

/**
 * Splits a `reqnroll/renameTargets` label of the form `"Given step text"` at the first space,
 * returning everything after the keyword prefix — the fallback source text for the rename input
 * box when `RenameTargetItem.expression` is empty (method-name-style bindings, issue #344).
 * Mirrors `RenameStepLabelParser.ExtractStepTextFromLabel` in the Visual Studio extension.
 */
export function extractStepTextFromLabel(label: string): string {
  const spaceIndex = label.indexOf(' ');
  const prefix = spaceIndex >= 0 ? label.slice(0, spaceIndex + 1) : '';
  return label.length > prefix.length ? label.slice(prefix.length) : label;
}

/**
 * Runs the Step Rename refactoring from a `.cs` binding attribute (issue #457).
 *
 * VS Code's built-in rename UI can't be reused here the way it is for `.feature` files: the
 * language client's `documentSelector` deliberately excludes `csharp` (its `.cs` sync is driven
 * manually — see `manualDocumentSync.ts`), so `vscode-languageclient` never registers a rename
 * provider for that language, and `editor.action.rename` would have nothing to invoke. Instead
 * this mirrors the Visual Studio extension's `RenameStepCommand`/`RenameStepService`: query
 * `reqnroll/renameTargets`, disambiguate via `QuickPick` when more than one binding sits at the
 * cursor (multiple `[Given]`/`[When]`/`[Then]` attributes on the same method), prompt for the new
 * step text with a plain input box pre-filled with the current expression, then send
 * `textDocument/rename` directly and apply the resulting `WorkspaceEdit` ourselves.
 *
 * `fallbackToNativeRename` (issue #506): when set, a miss at the cursor — not on a binding
 * expression, or not even a Reqnroll-owned file — delegates to VS Code's own `editor.action.rename`
 * instead of showing "No step definition found". This is what the F2 keybinding needs: F2 has to
 * work as an ordinary C# rename everywhere else in a `.cs` file (locals, methods, non-Reqnroll
 * projects), so silently eating the keystroke with an unrelated message would break the most-used
 * refactor shortcut in the language. The explicit "Reqnroll: Rename Step" context-menu/palette
 * command leaves this off — a user who deliberately picked that command wants to know their cursor
 * wasn't on a binding.
 */
export async function renameStepFromCSharp(
  client: LanguageClient,
  editor: vscode.TextEditor,
  options?: { fallbackToNativeRename?: boolean },
): Promise<void> {
  const uriStr = editor.document.uri.toString();
  const position = editor.selection.active;

  const targets = await getRenameTargets(client, uriStr, position, {
    requireAttributeLine: options?.fallbackToNativeRename,
  });
  if (targets.length === 0) {
    if (options?.fallbackToNativeRename) {
      await vscode.commands.executeCommand('editor.action.rename');
      return;
    }
    void vscode.window.showInformationMessage(
      'Reqnroll: No step definition found to rename at this position.',
    );
    return;
  }

  const chosen = targets.length === 1 ? targets[0] : await pickRenameTarget(targets);
  if (!chosen) return;

  await selectRenameTarget(client, uriStr, chosen.attributeIndex);

  const currentStepText = chosen.expression || extractStepTextFromLabel(chosen.label);
  const newStepText = await vscode.window.showInputBox({
    prompt: 'Enter the new step text:',
    value: currentStepText,
  });
  if (!newStepText) return;

  const result = await client.sendRequest<LspWorkspaceEdit | null>('textDocument/rename', {
    textDocument: { uri: uriStr },
    position: { line: position.line, character: position.character },
    newName: newStepText,
  });

  if (!result) {
    void vscode.window.showErrorMessage('Reqnroll: Rename failed.');
    return;
  }

  const edit = await client.protocol2CodeConverter.asWorkspaceEdit(result);
  await vscode.workspace.applyEdit(edit);
}
