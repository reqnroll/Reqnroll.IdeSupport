import * as path from 'path';
import { Builder, parseStringPromise } from 'xml2js';

/**
 * Pure runsettings-document merge for registering the bundled Reqnroll VSTest logger
 * (`Reqnroll.IdeSupport.TestLogger.dll`) into a run — the VS Code counterpart to VS's
 * `TestLoggerRunSettings.cs` (LSP-server outcome pipeline, #700/#702). Ported field-for-field
 * from that class; keep the two in sync if the wire contract changes.
 *
 * Unlike VS (which merges into whatever runsettings document `IRunSettingsService` was handed
 * for one specific run), VS Code has no per-run injection hook of its own — C# Dev Kit owns test
 * execution and reads a *file path* named by the `dotnet.unitTests.runSettingsPath` setting (see
 * `testOutcomesService.ts`). This module only does the document merge; the session-scoped
 * file/setting lifecycle lives one layer up.
 */

// Mirrors of ReqnrollIdeTestLogger's/TestLoggerRunSettings.cs's constants. Not imported from the
// logger project for the same reason the C# side gives: a build-time reference would pull the
// logger assembly into this extension's own output, which must stay independent of it.
export const LOGGER_FRIENDLY_NAME = 'ReqnrollIde';
export const LOGGER_EXTENSION_URI = 'logger://Reqnroll/IdeSupport/v1';
export const ENDPOINT_PARAMETER = 'Endpoint';
export const TOKEN_PARAMETER = 'Token';
export const RUN_ID_PARAMETER = 'RunId';
export const IDE_PROCESS_ID_PARAMETER = 'IdeProcessId';

/** Parameters to write into the injected `<Configuration>` element, in order — mirrors `IEnumerable<KeyValuePair<string,string>>` on the C# side. */
export type LoggerParameter = readonly [key: string, value: string];

// xml2js's default element shape: every child is an array (even when there's only ever one),
// and attributes live under "$". Kept minimal/untyped (not a full runsettings schema) since this
// module only ever touches the handful of elements below and passes everything else through
// `explicitArray: true`'s round-trip unchanged.
type XmlElement = { $?: Record<string, string>; [child: string]: unknown };
type RunSettingsDocument = { RunSettings?: [XmlElement] };

/**
 * Returns a new runsettings XML document with the logger registration merged in.
 *
 * @param inputXml The effective runsettings VS Code/C# Dev Kit would otherwise use — the
 *   user's own file's contents when one exists, or `undefined`/empty for "start fresh" (there is
 *   no VS-style "always some effective document" guarantee here; the caller resolves that).
 * @param loggerDirectory Absolute directory containing `Reqnroll.IdeSupport.TestLogger.dll`.
 * @param loggerParameters Children of the injected `<Configuration>` element, in order.
 */
export async function injectLogger(
  inputXml: string | undefined,
  loggerDirectory: string,
  loggerParameters: readonly LoggerParameter[],
): Promise<string> {
  const doc: RunSettingsDocument = await parseOrEmpty(inputXml);
  const runSettings = ensureChild(doc, 'RunSettings');

  const runConfiguration = ensureChild(runSettings, 'RunConfiguration');
  const existingPaths = firstText(runConfiguration, 'TestAdaptersPaths');
  runConfiguration.TestAdaptersPaths = [appendPath(existingPaths, loggerDirectory)];

  const loggerRunSettings = ensureChild(runSettings, 'LoggerRunSettings');
  const loggers = ensureChild(loggerRunSettings, 'Loggers');
  const remaining = ((loggers.Logger as XmlElement[] | undefined) ?? []).filter(
    (logger) =>
      !(
        logger.$?.friendlyName?.toLowerCase() === LOGGER_FRIENDLY_NAME.toLowerCase() ||
        logger.$?.uri?.toLowerCase() === LOGGER_EXTENSION_URI.toLowerCase()
      ),
  );

  const configuration: Record<string, string[]> = {};
  for (const [key, value] of loggerParameters) configuration[key] = [value];

  remaining.push({
    $: { friendlyName: LOGGER_FRIENDLY_NAME, enabled: 'True' },
    Configuration: [configuration],
  });
  loggers.Logger = remaining;

  return new Builder().buildObject(stripExplicitArray(doc));
}

/**
 * `parseStringPromise`'s `explicitArray: true` wraps every element in a one-item array so
 * "one child" and "many children of the same name" are unambiguous while reading/merging above —
 * without it, `Loggers.Logger` would come back as a bare object for exactly one existing logger
 * but an array for two or more, and every read site here would need to handle both shapes.
 * `Builder.buildObject`, however, does not accept that same convention back: an array at the
 * *root* (or any single-item wrapper) makes it try to build a numerically-named element and throw
 * `Invalid character in name` — confirmed directly against xml2js's `Builder`, not assumed. This
 * recursively unwraps every length-1 array to its bare item (leaving genuine multi-item arrays,
 * e.g. more than one `<Logger>`, as arrays, which the builder *does* handle correctly — one
 * element per item) so the same merged tree serializes correctly.
 */
function stripExplicitArray(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.length === 1 ? stripExplicitArray(value[0]) : value.map(stripExplicitArray);
  }
  if (value !== null && typeof value === 'object') {
    const result: Record<string, unknown> = {};
    for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
      result[key] = stripExplicitArray(child);
    }
    return result;
  }
  return value;
}

/** True when `xml` already carries our logger registration — used by tests and diagnostics. */
export function containsRegistration(loggers: XmlElement | undefined): boolean {
  const entries = (loggers?.Logger as XmlElement[] | undefined) ?? [];
  return entries.some(
    (logger) =>
      logger.$?.friendlyName?.toLowerCase() === LOGGER_FRIENDLY_NAME.toLowerCase() ||
      logger.$?.uri?.toLowerCase() === LOGGER_EXTENSION_URI.toLowerCase(),
  );
}

// ── Helpers ────────────────────────────────────────────────────────────────

/**
 * Confirmed directly against xml2js (not assumed): `explicitArray: true` wraps every
 * *descendant* element in a one-item array, but the parsed **root** element always comes back as
 * a bare object, one level un-wrapped from everything below it. Re-wraps the root here so the rest
 * of this module can treat `RunSettingsDocument.RunSettings` with the same "always an array"
 * convention as every other element — without this, `ensureChild`'s array check on the root
 * silently failed and discarded the entire parsed document instead of merging into it.
 */
async function parseOrEmpty(xml: string | undefined): Promise<RunSettingsDocument> {
  if (!xml || xml.trim().length === 0) return {};
  const parsed = (await parseStringPromise(xml, { explicitArray: true })) as {
    RunSettings?: XmlElement;
  };
  return parsed.RunSettings ? { RunSettings: [parsed.RunSettings] } : {};
}

function ensureChild(parent: XmlElement, name: string): XmlElement {
  const existing = parent[name] as XmlElement[] | undefined;
  if (existing && existing.length > 0 && typeof existing[0] === 'object') {
    return existing[0];
  }
  const created: XmlElement = {};
  parent[name] = [created];
  return created;
}

function firstText(parent: XmlElement, name: string): string | undefined {
  const value = parent[name] as unknown[] | undefined;
  if (!value || value.length === 0) return undefined;
  const first = value[0];
  return typeof first === 'string' ? first : undefined;
}

/**
 * vstest splits `TestAdaptersPaths` on `;` (`RunSettingsUtilities.GetTestAdaptersPaths`).
 * Existing entries are preserved verbatim; ours is appended unless an equivalent path is already
 * listed — mirrors `TestLoggerRunSettings.AppendPath`'s use of `PathUtils.IsSamePath` (issue #515
 * lesson: two independently-written path-comparison routines always eventually disagree — this
 * stays the one routine this module uses for that comparison).
 */
export function appendPath(existing: string | undefined, loggerDirectory: string): string {
  const parts = (existing ?? '')
    .split(';')
    .map((p) => p.trim())
    .filter((p) => p.length > 0);

  if (parts.some((p) => isSamePath(p, loggerDirectory))) {
    return existing ?? '';
  }

  parts.push(loggerDirectory);
  return parts.join(';');
}

/** Case-insensitive-on-Windows, `.`/`..`-collapsing path comparison — the one routine this module uses for "is this the same directory," mirroring `PathUtils.IsSamePath`. */
export function isSamePath(a: string, b: string): boolean {
  const normalize = (p: string) => {
    const resolved = path.resolve(p).replace(/\\/g, '/').replace(/\/+$/, '');
    return process.platform === 'win32' ? resolved.toLowerCase() : resolved;
  };
  return normalize(a) === normalize(b);
}
