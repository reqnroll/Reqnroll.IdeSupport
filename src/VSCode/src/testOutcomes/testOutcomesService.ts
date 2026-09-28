import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { ReqnrollMethods } from '../lsp/lspMethods';
import { resolveApplicationDirectory } from '../logging/logPaths';
import { logInfo, logWarn } from '../logging/appNotify';
import {
  ENDPOINT_PARAMETER,
  IDE_PROCESS_ID_PARAMETER,
  RUN_ID_PARAMETER,
  injectLogger,
  isSamePath,
} from './runSettingsInjector';
import { resolveTestLoggerDirectory } from './testLoggerPath';

/**
 * LSP-server outcome pipeline (#700/#702), VS Code leg. C# Dev Kit owns test discovery/execution
 * in VS Code (issue #504) — confirmed against the real `dotnet/vscode-csharp` source, the
 * `ms-dotnettools.csharp` extension it builds its testing UI on contributes a genuine,
 * general-purpose `dotnet.unitTests.runSettingsPath` setting: an ordinary shared VS Code
 * configuration key, not gated behind any C# Dev Kit-specific API, which any extension can read
 * and write. This module is the whole VS Code leg of getting the bundled VSTest logger into
 * whatever `dotnet test`/vstest invocation C# Dev Kit ends up running, via that setting, without
 * needing to read C# Dev Kit's own `vscode.tests` `TestController` at all — our logger reports to
 * our own LSP server over the loopback socket, exactly like VS/Rider, entirely independent of
 * whatever C# Dev Kit's own UI shows.
 *
 * **Session-scoped registration, not per-run.** VS and Rider re-register with the server for every
 * run because *their own code* launches the test process each time and can hand the fresh
 * registration to it on the way out. Here, C# Dev Kit launches the process, and there is no
 * confirmed VS Code event for "a test run is about to start" that this extension could hook to
 * refresh the runsettings file's content just beforehand — `vscode.tests` exports no such signal,
 * and C# Dev Kit's own run lifecycle isn't observable from outside it (same "no cross-controller"
 * finding recorded in docs/Test-Runner-Integration-Design.md for issue #504). Registration happens
 * once per extension activation instead (effectively: once per VS Code window session, refreshed on
 * reload/restart) and is merged into a Reqnroll-managed runsettings file that
 * `dotnet.unitTests.runSettingsPath` then points at for the rest of that session. This works safely
 * because the server's endpoint doesn't expire and carries no per-connection secret to go stale —
 * see `TestOutcomeTcpListener`'s remarks on the server side for why a per-run token was tried and
 * removed (it broke exactly this one-registration-many-connections shape).
 *
 * **Off by default, opt-in via `reqnroll.testOutcomes.enabled`.** Unlike everything else this
 * extension does, this writes to a setting namespaced under `dotnet.*`, not `reqnroll.*` — shared
 * with C# Dev Kit and potentially already customized by the user or their team, and persisted to
 * the workspace's own `.vscode/settings.json` (visible to teammates, possibly committed). The
 * user's existing runsettings content, if any, is always merged into (never replaced by) the
 * generated file this module points the setting at — see `runSettingsInjector.ts` — but the
 * setting *value* itself does change, which is enough of a visible side effect on shared state to
 * warrant requiring explicit opt-in rather than defaulting to on.
 *
 * **Opt-out cleanup and per-workspace file naming (issue #749).** Turning the feature off used to
 * leave the setting pointing at the generated file forever — there was no record of what the
 * setting held before this module first took it over, so there was nothing to restore it to, and
 * the file itself was shared by every open workspace (one fixed name under
 * `resolveApplicationDirectory()`), so two windows on different workspaces stomped on each other's
 * registration (whichever activated last "won", and the other's VSTest runs silently reported into
 * the wrong window). Both are fixed together: the generated file's name is now keyed by a hash of
 * the first workspace folder's path (`generatedFileName` below), so each
 * workspace gets its own file and windows on different workspaces stop colliding, and the setting's
 * value *before* this module ever wrote to it — including "it was unset" as a distinct state from
 * "it held path X" — is recorded once in `context.workspaceState` (`PRIOR_SETTING_STATE_KEY`) and
 * replayed back when the feature is next found disabled at activation, after which the recorded
 * state is cleared so a later re-enable records a fresh baseline. (Two windows on the exact same
 * workspace folder still share one `.vscode/settings.json` and therefore one generated file — that
 * is inherent to workspace-scoped settings, not something a filename change can fix, and is no
 * worse than any other workspace-scoped setting two windows on the same folder both try to own.)
 */

const RUNSETTINGS_CONFIG_SECTION = 'dotnet';
const RUNSETTINGS_CONFIG_KEY = 'unitTests.runSettingsPath';
// Deliberately lives in the application directory's root, not under resolveLogDirectory()'s
// `logs` subfolder (issue #726): this is a generated config file the user's own
// `dotnet.unitTests.runSettingsPath` setting points at, not a log, and its name starting with
// "reqnroll-" would otherwise make it eligible for pruneOldLogs' 10-day sweep if it ever sat in
// that directory.
const GENERATED_FILE_NAME_PREFIX = 'reqnroll-vscode-test-outcomes';
const GENERATED_FILE_NAME_SUFFIX = '.runsettings';
// Fallback seed for the (unusual) case of no open workspace folder — still keeps this module's
// file distinct from a real workspace's, though multiple no-folder windows will still collide;
// there is no folder identity to key on in that case.
const NO_WORKSPACE_FOLDER_SEED = 'no-workspace-folder';

/** `workspaceState` key: the `dotnet.unitTests.runSettingsPath` value from before this module first took it over. */
const PRIOR_SETTING_STATE_KEY = 'reqnroll.testOutcomes.priorRunSettingsPath';

/** Distinguishes "the setting was unset" from "the setting held an empty/falsy value" — `undefined` alone can't. */
interface PriorSettingState {
  wasSet: boolean;
  value?: string;
}

/**
 * Reads/writes `dotnet.unitTests.runSettingsPath`, injected so `recordPriorSettingIfNeeded` and
 * `cleanupRunSettingsOnOptOut` are testable without a real `dotnet.*` configuration contribution
 * present — that key is contributed by C# Dev Kit, not this extension, and does not exist at all
 * in a bare Extension Development Host that has it disabled (`workspace.getConfiguration(...)
 * .update(...)` throws "not a registered configuration" there). Mirrors the injected
 * `readTextOrNull`/`evaluate` parameters `mtpProjectStubs.ts` uses for the same reason.
 */
export interface RunSettingsConfigAccessor {
  get(): string | undefined;
  update(value: string | undefined): Thenable<void>;
}

function defaultRunSettingsConfigAccessor(): RunSettingsConfigAccessor {
  const config = vscode.workspace.getConfiguration(RUNSETTINGS_CONFIG_SECTION);
  return {
    get: () => config.get<string>(RUNSETTINGS_CONFIG_KEY),
    update: (value) =>
      config.update(RUNSETTINGS_CONFIG_KEY, value, vscode.ConfigurationTarget.Workspace),
  };
}

interface RegisterTestRunResponse {
  success: boolean;
  runId?: string;
  endpoint?: string;
}

/**
 * Registers this VS Code session with the LSP server and, if that succeeds, merges the bundled
 * logger into whatever `dotnet.unitTests.runSettingsPath` already resolves to. Never throws —
 * every failure (feature disabled, logger not bundled, server unreachable, file IO error) degrades
 * to "no server-sourced outcomes this session," logged but not surfaced as an error popup, since
 * this runs unprompted at activation and a popup would be disproportionate to a background,
 * opt-in convenience feature.
 *
 * When the feature is disabled, this now also undoes a prior opt-in (issue #749): if the setting
 * still points at our generated file, the value recorded before this module first took it over is
 * restored (or the key is cleared entirely if it was unset back then), and the generated file is
 * removed. See {@link cleanupRunSettingsOnOptOut}.
 */
export async function activateTestOutcomes(
  context: Pick<vscode.ExtensionContext, 'extensionMode' | 'extensionPath' | 'workspaceState'>,
  client: LanguageClient,
): Promise<void> {
  const enabled = vscode.workspace
    .getConfiguration('reqnroll')
    .get<boolean>('testOutcomes.enabled', false);
  if (!enabled) {
    try {
      await cleanupRunSettingsOnOptOut(context.workspaceState);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      logWarn(`testOutcomes: opt-out cleanup failed — ${msg}`);
    }
    return;
  }

  const loggerDirectory = resolveTestLoggerDirectory(context);
  if (!loggerDirectory) {
    logWarn(
      'testOutcomes: reqnroll.testOutcomes.enabled is true, but the bundled TestLogger was not found; skipping.',
    );
    return;
  }

  let registration: RegisterTestRunResponse | undefined;
  try {
    registration = await client.sendRequest<RegisterTestRunResponse>(
      ReqnrollMethods.registerTestRun,
      {},
    );
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    logWarn(`testOutcomes: registerTestRun request failed — ${msg}`);
    return;
  }

  if (!registration.success || !registration.endpoint || !registration.runId) {
    logWarn('testOutcomes: server declined to register a run this session; skipping.');
    return;
  }

  try {
    await mergeRunSettings(context.workspaceState, registration, loggerDirectory, process.pid);
    logInfo(
      `testOutcomes: registered run ${registration.runId} and merged the logger into '${RUNSETTINGS_CONFIG_KEY}'.`,
    );
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    logWarn(`testOutcomes: runsettings merge failed — ${msg}`);
  }
}

/**
 * Resolves the effective input runsettings (the user's own file if the setting already points
 * somewhere else; our own previously-generated file if it already points there; nothing if unset),
 * merges the logger in, writes the result to our managed file, and points the setting at it.
 * Records the setting's pre-takeover value in `workspaceState` the first time this runs for a
 * workspace that isn't already pointed at our file (issue #749), so a later opt-out can restore it.
 * `internal` shape kept as a plain function (not a class) — no state to hold beyond one call.
 */
async function mergeRunSettings(
  workspaceState: vscode.Memento,
  registration: RegisterTestRunResponse,
  loggerDirectory: string,
  ideProcessId: number,
  config: RunSettingsConfigAccessor = defaultRunSettingsConfigAccessor(),
): Promise<void> {
  const generatedPath = resolveGeneratedRunSettingsPath();
  const currentSetting = config.get();

  await recordPriorSettingIfNeeded(workspaceState, currentSetting, generatedPath);

  const inputXml = readInputRunSettings(currentSetting);

  const merged = await injectLogger(inputXml, loggerDirectory, [
    [ENDPOINT_PARAMETER, registration.endpoint!],
    [RUN_ID_PARAMETER, registration.runId!],
    [IDE_PROCESS_ID_PARAMETER, String(ideProcessId)],
  ]);

  fs.mkdirSync(path.dirname(generatedPath), { recursive: true });
  fs.writeFileSync(generatedPath, merged, 'utf8');

  if (!currentSetting || !isSamePath(currentSetting, generatedPath)) {
    await config.update(generatedPath);
  }
}

/**
 * Records `currentSetting` into `workspaceState` as the value to restore on opt-out — but only the
 * *first* time this module takes the setting over for this workspace. A `currentSetting` that
 * already matches our generated file means either a re-activation in the same session or an
 * earlier session that already recorded the true original value; either way, the existing record
 * (if any) must not be clobbered with our own prior value. Exported for testing.
 */
export async function recordPriorSettingIfNeeded(
  workspaceState: vscode.Memento,
  currentSetting: string | undefined,
  generatedPath: string,
): Promise<void> {
  const alreadyOurs = currentSetting !== undefined && isSamePath(currentSetting, generatedPath);
  if (alreadyOurs) return;

  const recorded = workspaceState.get<PriorSettingState>(PRIOR_SETTING_STATE_KEY);
  if (recorded) return;

  const toRecord: PriorSettingState =
    currentSetting === undefined ? { wasSet: false } : { wasSet: true, value: currentSetting };
  await workspaceState.update(PRIOR_SETTING_STATE_KEY, toRecord);
}

/**
 * Undoes a prior opt-in (issue #749): if `dotnet.unitTests.runSettingsPath` still points at our
 * generated file, restores the value recorded before this module first took it over (or clears the
 * key entirely if it was unset back then), then clears the recorded state so a later re-enable
 * records a fresh baseline. Always attempts to delete the generated file afterwards, regardless of
 * whether the setting needed restoring — a stale file left on disk still gets picked up if the
 * setting is ever pointed at it again by hand. Never throws; the caller logs failures. Exported for
 * testing.
 */
export async function cleanupRunSettingsOnOptOut(
  workspaceState: vscode.Memento,
  config: RunSettingsConfigAccessor = defaultRunSettingsConfigAccessor(),
): Promise<void> {
  const generatedPath = resolveGeneratedRunSettingsPath();
  const currentSetting = config.get();

  if (currentSetting !== undefined && isSamePath(currentSetting, generatedPath)) {
    const recorded = workspaceState.get<PriorSettingState>(PRIOR_SETTING_STATE_KEY);
    const restoreValue = recorded?.wasSet ? recorded.value : undefined;
    await config.update(restoreValue);
  }

  await workspaceState.update(PRIOR_SETTING_STATE_KEY, undefined);

  try {
    if (fs.existsSync(generatedPath)) fs.rmSync(generatedPath, { force: true });
  } catch {
    // Best-effort — a locked/unreadable file shouldn't turn opt-out cleanup into a hard failure.
  }
}

/**
 * The per-workspace generated runsettings file name (issue #749): a short hash of the first
 * workspace folder's path, so two windows on different workspaces never share a file and stomp on
 * each other's registration (endpoint/run id/IDE process id). Exported for testing.
 */
export function generatedFileName(workspaceFolderPath: string | undefined): string {
  const seed = workspaceFolderPath ?? NO_WORKSPACE_FOLDER_SEED;
  const hash = crypto.createHash('sha256').update(seed).digest('hex').slice(0, 12);
  return `${GENERATED_FILE_NAME_PREFIX}-${hash}${GENERATED_FILE_NAME_SUFFIX}`;
}

/**
 * {@link generatedFileName} resolved against the real current workspace and application
 * directory — what `mergeRunSettings`/`cleanupRunSettingsOnOptOut` compare `dotnet.unitTests
 * .runSettingsPath` against to decide "is this our file". Exported for testing, so a test seeding
 * a fake `RunSettingsConfigAccessor` can match it exactly.
 */
export function resolveGeneratedRunSettingsPath(): string {
  const workspaceFolderPath = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
  return path.join(resolveApplicationDirectory(), generatedFileName(workspaceFolderPath));
}

/**
 * Reads the runsettings content to merge our registration into. `currentSetting` is resolved
 * relative to the first workspace folder when it's a relative path — the same convention
 * `dotnet.unitTests.runSettingsPath` itself uses (its own documentation warns against
 * `${workspaceFolder}`-style variables for exactly this reason: it resolves paths directly against
 * the project root, not through VS Code's generic variable substitution). Returns `undefined`
 * (start fresh) when nothing is set, the configured file doesn't exist yet, or it's already our
 * own previously-generated file being re-merged (its content is what gets read back in that case —
 * this function doesn't special-case that path, it just reads whatever file is there).
 */
function readInputRunSettings(currentSetting: string | undefined): string | undefined {
  if (!currentSetting) return undefined;

  const resolved = path.isAbsolute(currentSetting)
    ? currentSetting
    : path.join(vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? '', currentSetting);

  if (!fs.existsSync(resolved)) return undefined;

  try {
    return fs.readFileSync(resolved, 'utf8');
  } catch {
    return undefined;
  }
}

// ── Outcome lookup (no consumer yet — ready for a future UI to call) ───────

/** One row (test case) of a method's last-known outcome — mirrors `TestOutcomeRowDto.cs` field-for-field. */
export interface TestOutcomeRow {
  displayName: string;
  outcome: string;
  durationMs: number;
  errorMessage?: string;
  stepCount: number;
  failedStepIndex?: number;
  failedStepText?: string;
  failedStepOutcome?: string;
}

/** Response for `reqnroll/testOutcomes/getOutcome` — mirrors `GetTestOutcomeResponse.cs` field-for-field. */
export interface GetTestOutcomeResponse {
  found: boolean;
  aggregate: string;
  rows: TestOutcomeRow[];
  isRunning: boolean;
  isStale: boolean;
}

/**
 * Runs `reqnroll/testOutcomes/getOutcome` for one generated test method. Returns `undefined` if
 * the request fails outright (no server running, transport error) — distinct from a successful
 * response with `found: false`, which means "no run has reported this method yet."
 *
 * Called from `testOutcomeCodeLens.ts`'s read-only outcome lens — see that module's doc comment
 * for why it carries no Run/Debug action (issue #504's history of reverted `.feature`-file UI
 * surfaces).
 */
export async function getTestOutcome(
  client: LanguageClient,
  assemblyPath: string,
  typeFullName: string,
  methodName: string,
): Promise<GetTestOutcomeResponse | undefined> {
  try {
    return await client.sendRequest<GetTestOutcomeResponse>(ReqnrollMethods.getTestOutcome, {
      assemblyPath,
      typeFullName,
      methodName,
    });
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    logWarn(`testOutcomes: getTestOutcome request failed — ${msg}`);
    return undefined;
  }
}
