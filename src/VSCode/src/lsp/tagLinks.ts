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
 * Middleware around the built-in `textDocument/documentLink` feature (issue #755): every returned
 * tag link is re-targeted by {@link retargetLink}, so clicking it runs {@link openTagLink}.
 */
export function createTagLinkMiddleware(): Middleware {
  return {
    provideDocumentLinks: async (document, token, next) => {
      const links = await next(document, token);
      return links
        ?.map(retargetLink)
        .filter((link): link is vscode.DocumentLink => link !== undefined);
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
