import * as vscode from 'vscode';
import {
  CancellationToken,
  LanguageClient,
  Middleware,
  PrepareRenameSignature,
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
 */
export async function getRenameTargets(
  client: LanguageClient,
  uriStr: string,
  position: vscode.Position,
): Promise<RenameTargetItem[]> {
  try {
    const response = await client.sendRequest<RenameTargetsResponse | null>(
      ReqnrollMethods.renameTargets,
      {
        textDocument: { uri: uriStr },
        position: { line: position.line, character: position.character },
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
 * Collapses an active text selection to a plain cursor before a `.feature` step rename is
 * delegated to VS Code's built-in rename UI (issue #456).
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
 * discussion on issue #456), collapse the pre-existing selection to `position` right before
 * delegating, so VS Code takes its own safe "no selection" path for a `.feature` step specifically.
 * A no-op for `.cs` files (and for a `.feature` file with no active selection), where the
 * preserved-selection behavior is already correct.
 */
export function collapseSelectionForFeatureStepRename(
  document: vscode.TextDocument,
  position: vscode.Position,
): void {
  if (document.languageId !== 'gherkin') return;

  const editor = vscode.window.activeTextEditor;
  if (editor?.document !== document || editor.selection.isEmpty) return;

  editor.selection = new vscode.Selection(position, position);
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
 * Also collapses an active selection before delegating to `next` for a `.feature` step, working
 * around a VS Code rename-widget quirk (issue #456) — see
 * {@link collapseSelectionForFeatureStepRename}.
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

      collapseSelectionForFeatureStepRename(document, position);

      return next(document, position, token);
    },
  };
}
