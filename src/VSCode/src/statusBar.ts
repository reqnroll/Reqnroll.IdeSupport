import * as vscode from 'vscode';
import { State } from 'vscode-languageclient';
import { LanguageClient } from 'vscode-languageclient/node';

/**
 * Manages the Reqnroll status bar item that reflects the LSP server lifecycle.
 *
 * Clicking the item runs `reqnroll.showOutputChannel` to reveal the server log.
 *
 * When `appLog` is supplied, each state transition is also mirrored there as a one-line entry
 * (issue #661) — the same curated "Reqnroll" channel command outcomes are mirrored to via
 * `logging/appNotify.ts` — so "is the extension doing something" has a single place to look.
 */
export class StatusBarManager implements vscode.Disposable {
  private readonly _item: vscode.StatusBarItem;
  private readonly _stateListener: vscode.Disposable;
  private readonly _appLog: vscode.LogOutputChannel | undefined;

  constructor(client: LanguageClient, appLog?: vscode.LogOutputChannel) {
    this._appLog = appLog;
    this._item = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
    this._item.command = 'reqnroll.showOutputChannel';
    this._setStarting();
    this._item.show();

    this._stateListener = client.onDidChangeState((event) => {
      switch (event.newState) {
        case State.Starting:
          this._setStarting();
          break;
        case State.Running:
          this._setRunning();
          break;
        case State.Stopped:
          this._setStopped();
          break;
      }
    });
  }

  dispose(): void {
    this._stateListener.dispose();
    this._item.dispose();
  }

  private _setStarting(): void {
    this._item.text = '$(loading~spin) Reqnroll';
    this._item.tooltip = 'Reqnroll LSP server starting…';
    this._item.backgroundColor = undefined;
    this._appLog?.info('Reqnroll LSP client starting…');
  }

  private _setRunning(): void {
    this._item.text = '$(check) Reqnroll';
    this._item.tooltip = 'Reqnroll LSP server running';
    this._item.backgroundColor = undefined;
    this._appLog?.info('Reqnroll LSP client connected.');
  }

  private _setStopped(): void {
    this._item.text = '$(error) Reqnroll';
    this._item.tooltip = 'Reqnroll LSP server stopped';
    this._item.backgroundColor = new vscode.ThemeColor('statusBarItem.errorBackground');
    this._appLog?.warn('Reqnroll LSP client stopped.');
  }
}
