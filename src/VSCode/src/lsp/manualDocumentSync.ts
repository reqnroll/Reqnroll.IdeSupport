import * as vscode from 'vscode';
import {
  LanguageClient,
  Middleware,
  DidOpenTextDocumentNotification,
  DidChangeTextDocumentNotification,
  DidCloseTextDocumentNotification,
  DidSaveTextDocumentNotification,
} from 'vscode-languageclient/node';

/** A predicate selecting which text documents a sync path is responsible for. */
export type DocumentPredicate = (document: vscode.TextDocument) => boolean;

/** True when `document` is a C# source file (matched by its `.cs` file extension). */
export function isCSharpDocument(document: vscode.TextDocument): boolean {
  return document.uri.path.toLowerCase().endsWith('.cs');
}

/**
 * Middleware that swallows vscode-languageclient's built-in text document synchronization for
 * documents matching `owns`, so a paired {@link ManualDocumentSync} can be their sole source of
 * sync notifications. Must be installed as `LanguageClientOptions.middleware` at
 * `LanguageClient` construction time -- before `client.start()` -- so it is already active
 * before the built-in feature's own initialization runs.
 */
export function createManualSyncMiddleware(owns: DocumentPredicate): Middleware {
  return {
    didOpen: (document, next) => (owns(document) ? Promise.resolve() : next(document)),
    didChange: (event, next) => (owns(event.document) ? Promise.resolve() : next(event)),
    didSave: (document, next) => (owns(document) ? Promise.resolve() : next(document)),
    didClose: (document, next) => (owns(document) ? Promise.resolve() : next(document)),
  };
}

/**
 * Manually drives `textDocument/didOpen|didChange|didSave|didClose` for documents matching
 * `owns`, entirely bypassing vscode-languageclient's built-in text document synchronization
 * feature for them.
 *
 * Why: that built-in feature has proven unreliable for `.cs` files here -- across repeated,
 * identical repros, `didOpen` (and therefore `didChange`) silently failed to reach the server in
 * a large fraction of runs, with the client remaining healthy ("Running") the entire time. Ruled
 * out by direct measurement: VS Code tab-restore races, `client/registerCapability` round-trip
 * timing, and silent client restarts -- the drop happens inside vscode-languageclient's own sync
 * bookkeeping. Driving sync manually guarantees exactly one path is ever active, using the
 * identical wire methods/params the library would have sent, so the server needs no changes.
 *
 * Must be paired with {@link createManualSyncMiddleware} using the same `owns` predicate in
 * `LanguageClientOptions.middleware` -- otherwise the built-in feature may also emit a sync
 * notification on the runs where it happens to work, producing duplicates.
 *
 * Extending to another document kind (e.g. `.feature`) later: pass a broader `owns` predicate,
 * or construct a second `ManualDocumentSync` paired with its own middleware predicate -- nothing
 * here is `.cs`-specific beyond what the caller passes in.
 */
export class ManualDocumentSync implements vscode.Disposable {
  private readonly openUris = new Set<string>();
  private readonly subscriptions: vscode.Disposable[];

  constructor(
    private readonly client: LanguageClient,
    private readonly owns: DocumentPredicate,
  ) {
    // Documents already open when this is constructed (e.g. restored tabs) never fire
    // onDidOpenTextDocument, so they must be synced explicitly here.
    for (const document of vscode.workspace.textDocuments) {
      if (this.owns(document)) this.sendDidOpen(document);
    }

    this.subscriptions = [
      vscode.workspace.onDidOpenTextDocument((document) => {
        if (this.owns(document) && !this.openUris.has(document.uri.toString())) {
          this.sendDidOpen(document);
        }
      }),
      vscode.workspace.onDidChangeTextDocument((event) => {
        // VS Code fires this event even with zero content changes (observed around save-time
        // formatting/EOL checks); forwarding those produces a redundant didChange with unchanged
        // version/content, so skip anything that isn't a real edit.
        if (event.contentChanges.length === 0) return;
        if (this.openUris.has(event.document.uri.toString())) this.sendDidChange(event.document);
      }),
      vscode.workspace.onDidSaveTextDocument((document) => {
        if (this.openUris.has(document.uri.toString())) this.sendDidSave(document);
      }),
      vscode.workspace.onDidCloseTextDocument((document) => {
        if (this.openUris.has(document.uri.toString())) this.sendDidClose(document);
      }),
    ];
  }

  dispose(): void {
    for (const sub of this.subscriptions) sub.dispose();
    this.openUris.clear();
  }

  private sendDidOpen(document: vscode.TextDocument): void {
    const uri = document.uri.toString();
    this.openUris.add(uri);
    void this.client.sendNotification(DidOpenTextDocumentNotification.type, {
      textDocument: {
        uri,
        languageId: document.languageId,
        version: document.version,
        text: document.getText(),
      },
    });
  }

  private sendDidChange(document: vscode.TextDocument): void {
    // Server registers TextDocumentSyncKind.Full for .cs, so a single full-text change
    // entry per notification matches what it expects (see TextDocumentSyncHandler.cs).
    void this.client.sendNotification(DidChangeTextDocumentNotification.type, {
      textDocument: { uri: document.uri.toString(), version: document.version },
      contentChanges: [{ text: document.getText() }],
    });
  }

  private sendDidSave(document: vscode.TextDocument): void {
    void this.client.sendNotification(DidSaveTextDocumentNotification.type, {
      textDocument: { uri: document.uri.toString() },
    });
  }

  private sendDidClose(document: vscode.TextDocument): void {
    const uri = document.uri.toString();
    this.openUris.delete(uri);
    void this.client.sendNotification(DidCloseTextDocumentNotification.type, {
      textDocument: { uri },
    });
  }
}
