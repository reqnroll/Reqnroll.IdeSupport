import * as vscode from 'vscode';
import {
  CancellationToken,
  LanguageClient,
  Middleware,
  PrepareRenameSignature,
} from 'vscode-languageclient/node';
import { ReqnrollMethods } from './lspMethods';

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
