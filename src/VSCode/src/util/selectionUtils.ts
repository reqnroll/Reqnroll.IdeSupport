/**
 * Normalises start/end lines for a VS Code selection before passing them to the
 * toggle-comment server command.
 *
 * VS Code reports the cursor at `(end.line, 0)` when the user drags past the
 * last character of a line without selecting any character on the next line.
 * In that case the final line should not be included in the range.
 */
export function normalizeSelectionLines(
  startLine: number,
  endLine: number,
  endChar: number,
): [number, number] {
  return endChar === 0 && endLine > startLine ? [startLine, endLine - 1] : [startLine, endLine];
}
