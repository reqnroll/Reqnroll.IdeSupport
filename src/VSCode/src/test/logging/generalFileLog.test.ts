import * as assert from 'assert';
import { formatLine } from '../../logging/generalFileLog';

// Portable subset of Reqnroll.IdeSupport.Common.Logging.LogLineFormatter.FormatPreamble shared
// with the .NET side and the Rider plugin's ReqnrollDebugLogger.formatLine (issue #626).
suite('formatLine', () => {
  test('renders a UTC ISO-8601 timestamp, a padded level, and the message', () => {
    const line = formatLine('Info', 'hello');
    assert.match(line, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \[Info {3}\] hello$/);
  });

  test('pads every real level to the same width', () => {
    assert.match(formatLine('Error', 'x'), /\[Error {2}\] x$/);
    assert.match(formatLine('Warning', 'x'), /\[Warning\] x$/);
    assert.match(formatLine('Info', 'x'), /\[Info {3}\] x$/);
    assert.match(formatLine('Verbose', 'x'), /\[Verbose\] x$/);
  });
});
