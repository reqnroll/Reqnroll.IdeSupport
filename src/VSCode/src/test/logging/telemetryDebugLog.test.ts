import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import {
  createTelemetryDebugLogFromValue,
  defaultTelemetryDebugLogPath,
} from '../../logging/telemetryDebugLog';

interface DebugLogRecord {
  source: string;
  event: string;
  props?: Record<string, unknown>;
  enabled?: boolean;
  transmitted?: boolean;
  error?: string;
}

function parseRecord(line: string): DebugLogRecord {
  return JSON.parse(line) as DebugLogRecord;
}

// Mirrors Reqnroll.IdeSupport.Common.Tests.Logging.TelemetryDebugLogTests (issue #799) — same
// resolution rules and record shape, so VS, VS Code and the LSP server produce one consistent
// reqnroll-telemetry-{date}.jsonl.
suite('createTelemetryDebugLogFromValue', () => {
  for (const value of [undefined, '', '   ', '0', 'false', 'False']) {
    test(`disables the sink for off value ${JSON.stringify(value)}`, () => {
      const sink = createTelemetryDebugLogFromValue(value);
      const before = fs.existsSync(defaultTelemetryDebugLogPath());
      sink.record('host', 'some event');
      assert.strictEqual(fs.existsSync(defaultTelemetryDebugLogPath()), before);
    });
  }

  test('treats any other value as a file path', () => {
    const target = path.join(os.tmpdir(), `reqnroll-tel-${Date.now()}.jsonl`);
    try {
      const sink = createTelemetryDebugLogFromValue(target);
      sink.record('host', 'Extension loaded', undefined, true, true);

      const lines = fs.readFileSync(target, 'utf8').trim().split('\n');
      assert.strictEqual(lines.length, 1);
      const parsed = parseRecord(lines[0]);
      assert.strictEqual(parsed.source, 'host');
      assert.strictEqual(parsed.event, 'Extension loaded');
      assert.strictEqual(parsed.enabled, true);
      assert.strictEqual(parsed.transmitted, true);
    } finally {
      if (fs.existsSync(target)) fs.unlinkSync(target);
    }
  });
});

suite('FileTelemetryDebugLog (via createTelemetryDebugLogFromValue)', () => {
  test('appends one JSON object per event, preserving property types', () => {
    const target = path.join(os.tmpdir(), `reqnroll-tel-${Date.now()}-${Math.random()}.jsonl`);
    try {
      const sink = createTelemetryDebugLogFromValue(target);

      sink.record('server', 'Reqnroll Discovery executed', {
        DiscoverySource: 'Connector',
        StepDefinitionCount: 42,
      });
      sink.record('host', 'Extension loaded', undefined, true, true);

      const lines = fs.readFileSync(target, 'utf8').trim().split('\n');
      assert.strictEqual(lines.length, 2);

      const first = parseRecord(lines[0]);
      assert.strictEqual(first.source, 'server');
      assert.strictEqual(first.event, 'Reqnroll Discovery executed');
      assert.strictEqual(first.props?.DiscoverySource, 'Connector');
      assert.strictEqual(first.props?.StepDefinitionCount, 42);

      const second = parseRecord(lines[1]);
      assert.strictEqual(second.source, 'host');
      assert.strictEqual(second.enabled, true);
      assert.strictEqual(second.transmitted, true);
    } finally {
      if (fs.existsSync(target)) fs.unlinkSync(target);
    }
  });

  test('never throws for an unwritable path', () => {
    const sink = createTelemetryDebugLogFromValue(
      process.platform === 'win32' ? 'Z:\\does\\not\\exist\\<>:|?.jsonl' : '/does/not/exist/\0bad',
    );
    assert.doesNotThrow(() => sink.record('server', 'E'));
  });
});

suite('defaultTelemetryDebugLogPath', () => {
  test('ends with a reqnroll-telemetry-<date>.jsonl file name under the shared log directory', () => {
    const p = defaultTelemetryDebugLogPath();
    assert.match(path.basename(p), /^reqnroll-telemetry-\d{8}\.jsonl$/);
  });
});
