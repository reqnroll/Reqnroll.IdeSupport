import * as vscode from 'vscode';
import { Middleware } from 'vscode-languageclient/node';
import { GHERKIN_LANGUAGE_ID } from '../languageIds';
import { sendTelemetryEvent } from '../telemetry';
import { TelemetryEvents } from '../telemetryEvents';

/** Internal command (hidden from the palette) that follows a clickable tag's link. */
export const OPEN_TAG_LINK_COMMAND = 'reqnroll.openTagLink';

/**
 * Only web links are opened: the target comes from `reqnroll.json`, which a cloned repository
 * controls, so `file:`, `command:` and other handler schemes are refused (issue #755).
 */
export function isOpenableUrl(target: string | undefined): target is string {
  if (!target) return false;
  try {
    const protocol = new URL(target).protocol;
    return protocol === 'http:' || protocol === 'https:';
  } catch {
    return false;
  }
}

/**
 * Re-targets one server-provided link at {@link OPEN_TAG_LINK_COMMAND} so a click reaches
 * the extension (VS Code would otherwise open the URL itself, invisibly to telemetry); the real URL
 * moves into the tooltip and the command's argument. Returns `undefined` for a link that is not an
 * openable web link, which drops it.
 */
export function retargetLink(link: vscode.DocumentLink): vscode.DocumentLink | undefined {
  const target = link.target?.toString();
  if (!isOpenableUrl(target)) return undefined;

  const commandUri = vscode.Uri.parse(
    `command:${OPEN_TAG_LINK_COMMAND}?${encodeURIComponent(JSON.stringify([target]))}`,
  );
  const retargeted = new vscode.DocumentLink(link.range, commandUri);
  retargeted.tooltip = link.tooltip ?? target;
  return retargeted;
}

/**
 * Keeps every clickable tag permanently styled as a link (issue #921). VS Code only underlines a
 * document link while Ctrl/Cmd is held over it, which hides which tags are links at all; this
 * remembers the ranges of the links the server returned for each document and paints them with a
 * link-coloured underline decoration. The ranges come from the same `provideDocumentLinks` pass
 * that renders the links, so they refresh exactly when the links do, and VS Code moves a
 * decoration along with edits made in between.
 */
export class TagLinkDecorations implements vscode.Disposable {
  private readonly rangesByDocument = new Map<string, vscode.Range[]>();
  private readonly decorationType: vscode.TextEditorDecorationType;
  private readonly subscriptions: vscode.Disposable[] = [];

  constructor(
    private readonly visibleEditors: () => readonly vscode.TextEditor[] = () =>
      vscode.window.visibleTextEditors,
    decorationType: vscode.TextEditorDecorationType = vscode.window.createTextEditorDecorationType({
      textDecoration: 'underline',
      color: new vscode.ThemeColor('textLink.foreground'),
    }),
  ) {
    this.decorationType = decorationType;
  }

  /** Starts following editor and document lifecycle; returns this for chaining. */
  register(): this {
    this.subscriptions.push(
      vscode.window.onDidChangeVisibleTextEditors(() => this.applyAll()),
      vscode.workspace.onDidCloseTextDocument((document) =>
        this.rangesByDocument.delete(document.uri.toString()),
      ),
    );
    return this;
  }

  /** Replaces the remembered link ranges of `uri` (an empty list clears them) and repaints its editors. */
  record(uri: vscode.Uri, ranges: readonly vscode.Range[]): void {
    this.rangesByDocument.set(uri.toString(), [...ranges]);
    for (const editor of this.visibleEditors()) {
      if (editor.document.uri.toString() === uri.toString()) this.apply(editor);
    }
  }

  /** The remembered link ranges of `uri`. */
  rangesFor(uri: vscode.Uri): readonly vscode.Range[] {
    return this.rangesByDocument.get(uri.toString()) ?? [];
  }

  private applyAll(): void {
    for (const editor of this.visibleEditors()) this.apply(editor);
  }

  private apply(editor: vscode.TextEditor): void {
    editor.setDecorations(this.decorationType, [...this.rangesFor(editor.document.uri)]);
  }

  dispose(): void {
    for (const subscription of this.subscriptions) subscription.dispose();
    this.decorationType.dispose();
    this.rangesByDocument.clear();
  }
}

/**
 * Middleware around the built-in `textDocument/documentLink` feature (issue #755): every returned
 * tag link is re-targeted by {@link retargetLink}, so clicking it runs {@link openTagLink}. When
 * `decorations` is given, the surviving links' ranges are also handed to it so they stay
 * permanently styled (issue #921).
 */
export function createTagLinkMiddleware(decorations?: TagLinkDecorations): Middleware {
  return {
    provideDocumentLinks: async (document, token, next) => {
      const links = await next(document, token);
      const retargeted = links
        ?.map(retargetLink)
        .filter((link): link is vscode.DocumentLink => link !== undefined);
      // A cancelled or failed request returns nothing (null): keep the last known ranges rather
      // than flashing the styling off; only an actual answer replaces them.
      if (retargeted && document?.uri) {
        decorations?.record(
          document.uri,
          retargeted.map((link) => link.range),
        );
      }
      return retargeted;
    },
  };
}

/**
 * Handler of {@link OPEN_TAG_LINK_COMMAND}: opens the URL in the default browser and reports the
 * "TagLink command executed" event (no properties - the URL and tag text are never sent).
 * Anything that is not an openable web link is ignored, whoever invoked the command.
 */
export async function openTagLink(
  url: unknown,
  openExternal: (uri: vscode.Uri) => Thenable<boolean> = (uri) => vscode.env.openExternal(uri),
): Promise<void> {
  if (typeof url !== 'string' || !isOpenableUrl(url)) return;
  sendTelemetryEvent(TelemetryEvents.tagLinkCommandExecuted);
  await openExternal(vscode.Uri.parse(url));
}

/**
 * Makes VS Code ask for the links again. VS Code requests `textDocument/documentLink` when a document
 * opens and after edits, never otherwise, and LSP has no `documentLink` refresh - so a restored tab that
 * asked before its project registered (the tag patterns live in the project's configuration) would keep
 * its empty answer until the user edited it. Registering and disposing a provider changes the link
 * provider registry, which makes every open editor recompute its links.
 */
export function nudgeDocumentLinkRefresh(): void {
  vscode.languages
    .registerDocumentLinkProvider(
      { language: GHERKIN_LANGUAGE_ID },
      { provideDocumentLinks: () => undefined },
    )
    .dispose();
}

/**
 * Re-requests the links whenever the server signals that binding discovery changed
 * (`workspace/codeLens/refresh`, shared through `getCodeLensRefreshEvent`), which is also when a project's
 * configuration first becomes available. Debounced: discovery can emit a burst of refreshes.
 */
export function registerTagLinkRefresh(
  refreshEvent: vscode.Event<void>,
  nudge: () => void = nudgeDocumentLinkRefresh,
  debounceMs = 300,
): vscode.Disposable {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const subscription = refreshEvent(() => {
    if (timer) clearTimeout(timer);
    timer = setTimeout(() => {
      timer = undefined;
      nudge();
    }, debounceMs);
  });
  return new vscode.Disposable(() => {
    if (timer) clearTimeout(timer);
    subscription.dispose();
  });
}
