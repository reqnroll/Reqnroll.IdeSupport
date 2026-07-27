import * as vscode from 'vscode';
import { State } from 'vscode-languageclient';
import { LanguageClient } from 'vscode-languageclient/node';

/**
 * Manages the Reqnroll status bar item that reflects the LSP server lifecycle.
 *
 * Clicking the item runs `reqnroll.showOutputChannel` to reveal the server log.
 */
export class StatusBarManager implements vscode.Disposable {
  private readonly _item: vscode.StatusBarItem;
  private readonly _stateListener: vscode.Disposable;

  constructor(client: LanguageClient) {
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
  }

  private _setRunning(): void {
    this._item.text = '$(check) Reqnroll';
    this._item.tooltip = 'Reqnroll LSP server running';
    this._item.backgroundColor = undefined;
  }

  private _setStopped(): void {
    this._item.text = '$(error) Reqnroll';
    this._item.tooltip = 'Reqnroll LSP server stopped';
    this._item.backgroundColor = new vscode.ThemeColor('statusBarItem.errorBackground');
  }
}
