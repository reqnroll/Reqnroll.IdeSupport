import * as vscode from 'vscode';
import {
  CancellationToken,
  Middleware,
  ProvideDefinitionSignature,
} from 'vscode-languageclient/node';
import { sendTelemetryEvent } from '../telemetry';
import { TelemetryEvents, TelemetryProperties } from '../telemetryEvents';

/** A navigation target returned by `textDocument/definition`. */
export interface DefinitionTarget {
  uri: string;
  startLine: number;
  endLine: number;
}

/** How long after a `textDocument/definition` response the editor may arrive at a target and still count. */
export const NAVIGATION_WINDOW_MS = 2000;

/**
 * Tells a genuine F12 / Ctrl+Click navigation from a hover. `textDocument/definition` is also
 * requested while the user merely hovers with Ctrl held (for the underline), so the request alone
 * proves nothing. A response is therefore remembered, and counted as a navigation only when the
 * active editor then lands on one of the targets it returned within {@link NAVIGATION_WINDOW_MS}.
 * A hover never moves the editor, so it never reports.
 */
export class DefinitionNavigationTracker {
  private pending: { targets: DefinitionTarget[]; at: number } | undefined;

  constructor(
    private readonly report: (locationCount: number) => void,
    private readonly now: () => number = Date.now,
    private readonly windowMs: number = NAVIGATION_WINDOW_MS,
  ) {}

  /** Remembers the targets of a definition response; an empty response clears any earlier one. */
  noteDefinitionResponse(targets: readonly DefinitionTarget[]): void {
    this.pending = targets.length > 0 ? { targets: [...targets], at: this.now() } : undefined;
  }

  /** Reports once if the editor has arrived at a remembered target in time; otherwise does nothing. */
  noteEditorPosition(uri: string, line: number): void {
    const pending = this.pending;
    if (!pending) return;
    if (this.now() - pending.at > this.windowMs) {
      this.pending = undefined;
      return;
    }
    const arrived = pending.targets.some(
      (t) => t.uri === uri && line >= t.startLine && line <= t.endLine,
    );
    if (!arrived) return;
    this.pending = undefined;
    this.report(pending.targets.length);
  }
}

/** Normalises the `Definition | DefinitionLink[]` shapes a definition provider may return. */
export function toDefinitionTargets(
  result: vscode.Definition | vscode.DefinitionLink[] | null | undefined,
): DefinitionTarget[] {
  if (!result) return [];
  const items: Array<vscode.Location | vscode.DefinitionLink> = Array.isArray(result)
    ? result
    : [result];
  return items.map((item) => {
    const isLink = 'targetUri' in item;
    const uri = isLink ? item.targetUri : item.uri;
    const range = isLink ? (item.targetSelectionRange ?? item.targetRange) : item.range;
    return { uri: uri.toString(), startLine: range.start.line, endLine: range.end.line };
  });
}

/** Sends `Command.GoToStepDefinition` for a navigation the tracker has confirmed. */
export function reportDefinitionNavigation(locationCount: number): void {
  sendTelemetryEvent(TelemetryEvents.commandGoToStepDefinition, {
    [TelemetryProperties.locationCount]: locationCount,
  });
}

/** Feeds the tracker with the definition responses the language client receives. */
export function createDefinitionNavigationMiddleware(
  tracker: DefinitionNavigationTracker,
): Middleware {
  return {
    provideDefinition: async (
      document: vscode.TextDocument,
      position: vscode.Position,
      token: CancellationToken,
      next: ProvideDefinitionSignature,
    ) => {
      const result = await next(document, position, token);
      tracker.noteDefinitionResponse(toDefinitionTargets(result));
      return result;
    },
  };
}

/** Feeds the tracker with where the active editor goes; dispose with the extension. */
export function registerDefinitionNavigationTracking(
  tracker: DefinitionNavigationTracker,
): vscode.Disposable {
  const noteActive = (editor: vscode.TextEditor | undefined) => {
    if (editor)
      tracker.noteEditorPosition(editor.document.uri.toString(), editor.selection.active.line);
  };
  return vscode.Disposable.from(
    vscode.window.onDidChangeActiveTextEditor(noteActive),
    vscode.window.onDidChangeTextEditorSelection((e) => noteActive(e.textEditor)),
  );
}
