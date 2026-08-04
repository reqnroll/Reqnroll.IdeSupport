import * as vscode from 'vscode';

/**
 * Adds per-pipe-character and per-cell decorations to Gherkin data tables.
 *
 * LSP semantic tokens can only express one token type per span, so the server's
 * `reqnroll.data_table` / `reqnroll.data_table_header` legend entries (see
 * ReqnrollClassificationTypeNames.cs) can distinguish a header row from a body row, but they
 * cannot separately style the `|` pipe characters from the cell text within a row. This service
 * fills that gap client-side, the same way Visual Studio's legacy classifier could not either —
 * it's an editor-only enhancement, not a port of existing VS behavior.
 *
 * Ported from Thomas Heijtink's proof-of-concept (github.com/ThomasHeijtink/Reqnroll.Plugin,
 * `Reqnroll.Plugin.VsCode/src/tableHighlightService.ts`).
 */
export class TableHighlightService implements vscode.Disposable {
  private readonly headerDecoration = vscode.window.createTextEditorDecorationType({
    color: '#8A2DA5',
    fontWeight: 'bold',
  });

  private readonly cellDecoration = vscode.window.createTextEditorDecorationType({
    color: '#C74A2F',
  });

  private readonly pipeDecoration = vscode.window.createTextEditorDecorationType({
    color: '#2F45FF',
  });

  private readonly disposables: vscode.Disposable[] = [];

  constructor() {
    this.disposables.push(
      this.headerDecoration,
      this.cellDecoration,
      this.pipeDecoration,
      vscode.window.onDidChangeActiveTextEditor(() => this.refreshVisibleEditors()),
      vscode.window.onDidChangeVisibleTextEditors(() => this.refreshVisibleEditors()),
      vscode.workspace.onDidChangeTextDocument((event) => this.refreshDocument(event.document)),
      vscode.workspace.onDidOpenTextDocument((document) => this.refreshDocument(document)),
    );

    this.refreshVisibleEditors();
  }

  dispose(): void {
    for (const disposable of this.disposables) {
      disposable.dispose();
    }
  }

  private refreshVisibleEditors(): void {
    for (const editor of vscode.window.visibleTextEditors) {
      this.refreshEditor(editor);
    }
  }

  private refreshDocument(document: vscode.TextDocument): void {
    for (const editor of vscode.window.visibleTextEditors) {
      if (editor.document.uri.toString() === document.uri.toString()) {
        this.refreshEditor(editor);
      }
    }
  }

  private refreshEditor(editor: vscode.TextEditor): void {
    if (editor.document.languageId !== 'gherkin') {
      return;
    }

    const headerRanges: vscode.Range[] = [];
    const cellRanges: vscode.Range[] = [];
    const pipeRanges: vscode.Range[] = [];

    for (let lineNumber = 0; lineNumber < editor.document.lineCount; lineNumber++) {
      const textLine = editor.document.lineAt(lineNumber);
      if (!isTableRow(textLine.text)) {
        continue;
      }

      const isHeaderRow =
        lineNumber === 0 || !isTableRow(editor.document.lineAt(lineNumber - 1).text);
      const pipeIndexes = getPipeIndexes(textLine.text);

      for (const pipeIndex of pipeIndexes) {
        pipeRanges.push(new vscode.Range(lineNumber, pipeIndex, lineNumber, pipeIndex + 1));
      }

      for (let i = 0; i < pipeIndexes.length - 1; i++) {
        const contentRange = getTrimmedCellRange(
          lineNumber,
          textLine.text,
          pipeIndexes[i],
          pipeIndexes[i + 1],
        );
        if (!contentRange) {
          continue;
        }

        if (isHeaderRow) {
          headerRanges.push(contentRange);
        } else {
          cellRanges.push(contentRange);
        }
      }
    }

    editor.setDecorations(this.headerDecoration, headerRanges);
    editor.setDecorations(this.cellDecoration, cellRanges);
    editor.setDecorations(this.pipeDecoration, pipeRanges);
  }
}

/** True when `text` is a Gherkin data-table row: starts with `|` and contains at least two pipes. */
export function isTableRow(text: string): boolean {
  const trimmedStart = text.trimStart();
  if (!trimmedStart.startsWith('|')) {
    return false;
  }

  let pipeCount = 0;
  for (const character of text) {
    if (character === '|') {
      pipeCount++;
      if (pipeCount >= 2) {
        return true;
      }
    }
  }

  return false;
}

/** Returns the character indexes of every `|` in `text`. */
export function getPipeIndexes(text: string): number[] {
  const result: number[] = [];
  for (let index = 0; index < text.length; index++) {
    if (text[index] === '|') {
      result.push(index);
    }
  }

  return result;
}

/**
 * Returns the range of a table cell's content between two pipe characters, with leading and
 * trailing whitespace trimmed off, or `undefined` if the cell is empty/whitespace-only.
 */
export function getTrimmedCellRange(
  lineNumber: number,
  text: string,
  leftPipeIndex: number,
  rightPipeIndex: number,
): vscode.Range | undefined {
  let start = leftPipeIndex + 1;
  let end = rightPipeIndex;

  while (start < end && /\s/.test(text[start])) {
    start++;
  }

  while (end > start && /\s/.test(text[end - 1])) {
    end--;
  }

  if (end <= start) {
    return undefined;
  }

  return new vscode.Range(lineNumber, start, lineNumber, end);
}
