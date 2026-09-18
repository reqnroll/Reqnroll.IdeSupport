import * as assert from 'assert';
import { parseStringPromise } from 'xml2js';
import {
  LOGGER_EXTENSION_URI,
  LOGGER_FRIENDLY_NAME,
  appendPath,
  containsRegistration,
  injectLogger,
  isSamePath,
} from '../../testOutcomes/runSettingsInjector';

const LOGGER_DIR = 'C:\\ext\\testlogger';

/**
 * Minimal shape of what `parseStringPromise` (default `explicitArray: true`) returns for a merged
 * runsettings document — just enough for these assertions, not a full schema. Every level is
 * declared required (rather than matching the true optionality of arbitrary XML) since every test
 * below only reads a level after `injectLogger` has guaranteed it exists.
 */
interface ParsedLogger {
  $: { friendlyName?: string; uri?: string; enabled?: string };
  Configuration: [Record<string, [string]>];
}
interface ParsedRunSettings {
  RunSettings: {
    RunConfiguration: [{ TestAdaptersPaths: [string] }];
    LoggerRunSettings: [{ Loggers: [{ Logger: ParsedLogger[] }] }];
  };
}

async function parseRunSettings(xml: string): Promise<ParsedRunSettings> {
  return (await parseStringPromise(xml)) as ParsedRunSettings;
}

suite('runSettingsInjector', () => {
  suite('injectLogger', () => {
    test('starting from nothing, creates RunSettings/RunConfiguration/TestAdaptersPaths and the Logger entry', async () => {
      const xml = await injectLogger(undefined, LOGGER_DIR, [
        ['Endpoint', '127.0.0.1:5000'],
        ['Token', 'tok'],
        ['RunId', 'run-1'],
      ]);

      const doc = await parseRunSettings(xml);
      assert.strictEqual(doc.RunSettings.RunConfiguration[0].TestAdaptersPaths[0], LOGGER_DIR);

      const logger = doc.RunSettings.LoggerRunSettings[0].Loggers[0].Logger[0];
      assert.strictEqual(logger.$.friendlyName, LOGGER_FRIENDLY_NAME);
      assert.strictEqual(logger.$.enabled, 'True');
      assert.strictEqual(logger.Configuration[0].Endpoint[0], '127.0.0.1:5000');
      assert.strictEqual(logger.Configuration[0].Token[0], 'tok');
      assert.strictEqual(logger.Configuration[0].RunId[0], 'run-1');
    });

    test('preserves an existing TestAdaptersPaths entry and appends ours', async () => {
      const input = `<RunSettings><RunConfiguration><TestAdaptersPaths>C:\\other\\adapters</TestAdaptersPaths></RunConfiguration></RunSettings>`;

      const xml = await injectLogger(input, LOGGER_DIR, [
        ['Endpoint', 'e'],
        ['Token', 't'],
        ['RunId', 'r'],
      ]);

      const doc = await parseRunSettings(xml);
      assert.strictEqual(
        doc.RunSettings.RunConfiguration[0].TestAdaptersPaths[0],
        `C:\\other\\adapters;${LOGGER_DIR}`,
      );
    });

    test('does not duplicate an already-listed adapter path (case/segment-insensitive)', async () => {
      const input = `<RunSettings><RunConfiguration><TestAdaptersPaths>${LOGGER_DIR.toUpperCase()}</TestAdaptersPaths></RunConfiguration></RunSettings>`;

      const xml = await injectLogger(input, LOGGER_DIR, [
        ['Endpoint', 'e'],
        ['Token', 't'],
        ['RunId', 'r'],
      ]);

      const doc = await parseRunSettings(xml);
      assert.strictEqual(
        doc.RunSettings.RunConfiguration[0].TestAdaptersPaths[0],
        LOGGER_DIR.toUpperCase(),
      );
    });

    test('preserves an unrelated existing logger and adds ours alongside it', async () => {
      const input = `<RunSettings><LoggerRunSettings><Loggers><Logger friendlyName="console" enabled="True" /></Loggers></LoggerRunSettings></RunSettings>`;

      const xml = await injectLogger(input, LOGGER_DIR, [
        ['Endpoint', 'e'],
        ['Token', 't'],
        ['RunId', 'r'],
      ]);

      const doc = await parseRunSettings(xml);
      const loggers = doc.RunSettings.LoggerRunSettings[0].Loggers[0].Logger;
      assert.strictEqual(loggers.length, 2);
      assert.ok(loggers.some((l) => l.$.friendlyName === 'console'));
      assert.ok(loggers.some((l) => l.$.friendlyName === LOGGER_FRIENDLY_NAME));
    });

    test('re-injecting replaces our own prior registration rather than duplicating it', async () => {
      const first = await injectLogger(undefined, LOGGER_DIR, [
        ['Endpoint', 'old'],
        ['Token', 'old'],
        ['RunId', 'old'],
      ]);

      const second = await injectLogger(first, LOGGER_DIR, [
        ['Endpoint', 'new'],
        ['Token', 'new'],
        ['RunId', 'new'],
      ]);

      const doc = await parseRunSettings(second);
      const loggers = doc.RunSettings.LoggerRunSettings[0].Loggers[0].Logger;
      assert.strictEqual(loggers.length, 1);
      assert.strictEqual(loggers[0].Configuration[0].Endpoint[0], 'new');
    });

    test('also replaces a stale registration matched by extension URI rather than friendly name', async () => {
      const input = `<RunSettings><LoggerRunSettings><Loggers><Logger uri="${LOGGER_EXTENSION_URI}" enabled="True"><Configuration><Endpoint>stale</Endpoint></Configuration></Logger></Loggers></LoggerRunSettings></RunSettings>`;

      const xml = await injectLogger(input, LOGGER_DIR, [
        ['Endpoint', 'fresh'],
        ['Token', 't'],
        ['RunId', 'r'],
      ]);

      const doc = await parseRunSettings(xml);
      const loggers = doc.RunSettings.LoggerRunSettings[0].Loggers[0].Logger;
      assert.strictEqual(loggers.length, 1);
      assert.strictEqual(loggers[0].Configuration[0].Endpoint[0], 'fresh');
    });
  });

  suite('appendPath', () => {
    test('appends to an empty/undefined existing value', () => {
      assert.strictEqual(appendPath(undefined, LOGGER_DIR), LOGGER_DIR);
      assert.strictEqual(appendPath('', LOGGER_DIR), LOGGER_DIR);
    });

    test('appends after existing entries, semicolon-separated', () => {
      assert.strictEqual(appendPath('C:\\a;C:\\b', LOGGER_DIR), `C:\\a;C:\\b;${LOGGER_DIR}`);
    });

    test('is a no-op (returns the input verbatim) when an equivalent path is already listed', () => {
      const existing = `C:\\a;${LOGGER_DIR}`;
      assert.strictEqual(appendPath(existing, LOGGER_DIR), existing);
    });
  });

  suite('isSamePath', () => {
    test('matches regardless of trailing separator', () => {
      assert.ok(isSamePath('C:\\ext\\testlogger', 'C:\\ext\\testlogger\\'));
    });

    test('matches regardless of slash direction', () => {
      assert.ok(isSamePath('C:\\ext\\testlogger', 'C:/ext/testlogger'));
    });

    test('does not match distinct directories', () => {
      assert.ok(!isSamePath('C:\\ext\\a', 'C:\\ext\\b'));
    });
  });

  suite('containsRegistration', () => {
    test('is false for undefined/empty Loggers', () => {
      assert.strictEqual(containsRegistration(undefined), false);
      assert.strictEqual(containsRegistration({}), false);
    });

    test('is true when a Logger entry matches by friendly name', () => {
      assert.strictEqual(
        containsRegistration({ Logger: [{ $: { friendlyName: LOGGER_FRIENDLY_NAME } }] }),
        true,
      );
    });
  });
});
