import * as vscode from 'vscode';

/**
 * How long after `onWillSaveTextDocument` a document still counts as "being saved" if
 * `onDidSaveTextDocument` never arrives (a failed or cancelled save). Generous, because a
 * save with slow save participants can legitimately take several seconds.
 */
export const SAVE_WINDOW_MS = 10_000;

/**
 * Tracks which documents are in the middle of a save, so a format request that VS Code issues from
 * `editor.formatOnSave` can be told apart from an explicit Format Document.
 *
 * `vscode-languageclient`'s formatting middleware gets no hint about what triggered the request, but
 * the save sequence does: verified empirically in the extension host that `onWillSaveTextDocument`
 * fires **before** the format-on-save provider call and `onDidSaveTextDocument` after it, whereas an
 * explicit `editor.action.formatDocument` / `formatSelection` runs outside that window.
 *
 * Keyed by document URI string. Not a singleton on purpose: the clock is injectable for tests.
 */
export class SaveTracker {
  private readonly savingSince = new Map<string, number>();

  constructor(private readonly now: () => number = Date.now) {}

  /** A save of `uri` is about to run its participants (including format-on-save). */
  begin(uri: string): void {
    this.savingSince.set(uri, this.now());
  }

  /** The save of `uri` finished. */
  end(uri: string): void {
    this.savingSince.delete(uri);
  }

  /** True while `uri` is being saved, and for at most {@link SAVE_WINDOW_MS} after the save began. */
  isSaving(uri: string): boolean {
    const since = this.savingSince.get(uri);
    if (since === undefined) return false;
    if (this.now() - since > SAVE_WINDOW_MS) {
      this.savingSince.delete(uri);
      return false;
    }
    return true;
  }
}

/** The tracker the formatting middleware consults; fed by {@link registerSaveTracking}. */
export const saveTracker = new SaveTracker();

/** Subscribes {@link saveTracker} to the workspace's will-save / did-save events. */
export function registerSaveTracking(): vscode.Disposable {
  return vscode.Disposable.from(
    vscode.workspace.onWillSaveTextDocument((e) => saveTracker.begin(e.document.uri.toString())),
    vscode.workspace.onDidSaveTextDocument((d) => saveTracker.end(d.uri.toString())),
  );
}
