import * as assert from 'assert';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import {
  collectMethodSymbols,
  discoverFeatureFiles,
  ensureFileItem,
  populateScenarios,
  runHandler,
} from '../../testRunner/reqnrollTestController';
import { DotnetTestRunResult } from '../../testRunner/dotnetTestRunner';
import { ScenarioTestTargetDto } from '../../testRunner/scenarioTestTarget';

function methodSymbol(
  name: string,
  line: number,
  children: vscode.DocumentSymbol[] = [],
): vscode.DocumentSymbol {
  const range = new vscode.Range(line, 0, line + 1, 0);
  const symbol = new vscode.DocumentSymbol(name, '', vscode.SymbolKind.Method, range, range);
  symbol.children.push(...children);
  return symbol;
}

function namespaceSymbol(name: string, children: vscode.DocumentSymbol[]): vscode.DocumentSymbol {
  const range = new vscode.Range(0, 0, 10, 0);
  const symbol = new vscode.DocumentSymbol(name, '', vscode.SymbolKind.Namespace, range, range);
  symbol.children.push(...children);
  return symbol;
}

function withStubbedExecuteCommand<T>(
  stub: (command: string, ...rest: unknown[]) => Thenable<unknown>,
  fn: () => Promise<T>,
): Promise<T> {
  const original = vscode.commands.executeCommand;
  (vscode.commands as unknown as { executeCommand: unknown }).executeCommand = stub;
  return fn().finally(() => {
    (vscode.commands as unknown as { executeCommand: unknown }).executeCommand = original;
  });
}

function withStubbedFindFiles<T>(
  stub: (...args: unknown[]) => Thenable<vscode.Uri[]>,
  fn: () => Promise<T>,
): Promise<T> {
  const original = vscode.workspace.findFiles;
  (vscode.workspace as unknown as { findFiles: unknown }).findFiles = stub;
  return fn().finally(() => {
    (vscode.workspace as unknown as { findFiles: unknown }).findFiles = original;
  });
}

function target(overrides: Partial<ScenarioTestTargetDto> = {}): ScenarioTestTargetDto {
  return {
    declaringTypeFullName: 'Tests.FFeature',
    methodName: 'AddNumbers',
    isParameterized: false,
    rowArguments: null,
    rowIndex: null,
    ...overrides,
  };
}

function fakeClient(sendRequest: (method: string, params: unknown) => Promise<unknown>): LanguageClient {
  return { sendRequest } as unknown as LanguageClient;
}

/** `.fsPath` (not a raw string) — `findOwningProjectFile` compares OS-native paths, and a hand-typed POSIX-style string mismatches Windows's backslash `fsPath` output. */
function fakeProjectManager(knownProjects: readonly string[]) {
  return { getKnownProjects: () => new Set(knownProjects) };
}

let controllerCounter = 0;
/** A real `vscode.TestController` — its `items`/`createTestItem` behavior is exercised for real; only `createTestRun` gets overridden by run-handler tests below. */
function createController(): vscode.TestController {
  return vscode.tests.createTestController(`reqnroll-test-${controllerCounter++}`, 'Test');
}

const noToken = { isCancellationRequested: false } as vscode.CancellationToken;

function requestFor(include: vscode.TestItem[]): vscode.TestRunRequest {
  return { include, exclude: undefined, profile: undefined, preserveFocus: true };
}

/** `TestMessage.message` is `string | MarkdownString` — extracts plain text either way for assertions. */
function messageText(message: vscode.TestMessage): string {
  return typeof message.message === 'string' ? message.message : message.message.value;
}

suite('reqnrollTestController', () => {
  suite('collectMethodSymbols', () => {
    test('collects a top-level Method symbol', () => {
      const symbols = [methodSymbol('Add two numbers', 1)];
      const result = collectMethodSymbols(symbols);
      assert.strictEqual(result.length, 1);
      assert.strictEqual(result[0].name, 'Add two numbers');
    });

    test('descends into Rule (Namespace-kind) children to find nested scenarios', () => {
      const scenario = methodSymbol('Nested scenario', 3);
      const rule = namespaceSymbol('My Rule', [scenario]);
      const result = collectMethodSymbols([rule]);
      assert.strictEqual(result.length, 1);
      assert.strictEqual(result[0].name, 'Nested scenario');
    });

    test('ignores non-Method symbols (Step/Examples/Background) at any depth', () => {
      const step = new vscode.DocumentSymbol(
        'Given a step',
        '',
        vscode.SymbolKind.Field,
        new vscode.Range(2, 0, 2, 1),
        new vscode.Range(2, 0, 2, 1),
      );
      const scenario = methodSymbol('S', 1, [step]);
      const result = collectMethodSymbols([scenario]);
      assert.strictEqual(result.length, 1);
      assert.strictEqual(result[0].name, 'S');
    });
  });

  suite('discoverFeatureFiles / ensureFileItem', () => {
    test('adds one container item per discovered .feature file', async () => {
      const controller = createController();
      try {
        const uris = [vscode.Uri.parse('file:///A.feature'), vscode.Uri.parse('file:///B.feature')];
        await withStubbedFindFiles(
          () => Promise.resolve(uris),
          () => discoverFeatureFiles(controller),
        );

        const ids: string[] = [];
        controller.items.forEach((item) => ids.push(item.id));
        assert.deepStrictEqual(ids.sort(), [...uris.map((u) => u.toString())].sort());
      } finally {
        controller.dispose();
      }
    });

    test('reuses the existing item for a URI already known', () => {
      const controller = createController();
      try {
        const uri = vscode.Uri.parse('file:///A.feature');
        const first = ensureFileItem(controller, uri);
        const second = ensureFileItem(controller, uri);
        assert.strictEqual(first, second);
      } finally {
        controller.dispose();
      }
    });
  });

  suite('populateScenarios', () => {
    test('places one child item per scenario symbol', async () => {
      const controller = createController();
      try {
        const fileItem = ensureFileItem(controller, vscode.Uri.parse('file:///F.feature'));
        await withStubbedExecuteCommand(
          () => Promise.resolve([methodSymbol('S', 1)]),
          () => populateScenarios(controller, fileItem),
        );

        assert.strictEqual(fileItem.children.size, 1);
      } finally {
        controller.dispose();
      }
    });

    test('finds scenarios nested under a Rule via executeDocumentSymbolProvider', async () => {
      const controller = createController();
      try {
        const fileItem = ensureFileItem(controller, vscode.Uri.parse('file:///F.feature'));
        const rule = namespaceSymbol('R', [methodSymbol('Nested', 3)]);
        await withStubbedExecuteCommand(
          () => Promise.resolve([rule]),
          () => populateScenarios(controller, fileItem),
        );

        assert.strictEqual(fileItem.children.size, 1);
      } finally {
        controller.dispose();
      }
    });

    test('clears children when the document has no symbols', async () => {
      const controller = createController();
      try {
        const fileItem = ensureFileItem(controller, vscode.Uri.parse('file:///F.feature'));
        await withStubbedExecuteCommand(
          () => Promise.resolve(undefined),
          () => populateScenarios(controller, fileItem),
        );

        assert.strictEqual(fileItem.children.size, 0);
      } finally {
        controller.dispose();
      }
    });
  });

  suite('runHandler', () => {
    interface RecordedRun {
      readonly enqueued: vscode.TestItem[];
      readonly started: vscode.TestItem[];
      readonly passed: vscode.TestItem[];
      readonly failed: Array<{ item: vscode.TestItem; message: vscode.TestMessage }>;
      readonly errored: Array<{ item: vscode.TestItem; message: vscode.TestMessage }>;
      readonly skipped: vscode.TestItem[];
      ended: boolean;
    }

    function withRecordingRun(controller: vscode.TestController): RecordedRun {
      const recorded: RecordedRun = {
        enqueued: [],
        started: [],
        passed: [],
        failed: [],
        errored: [],
        skipped: [],
        ended: false,
      };
      const fakeRun = {
        enqueued: (item: vscode.TestItem) => recorded.enqueued.push(item),
        started: (item: vscode.TestItem) => recorded.started.push(item),
        passed: (item: vscode.TestItem) => recorded.passed.push(item),
        failed: (item: vscode.TestItem, message: vscode.TestMessage) =>
          recorded.failed.push({ item, message }),
        errored: (item: vscode.TestItem, message: vscode.TestMessage) =>
          recorded.errored.push({ item, message }),
        skipped: (item: vscode.TestItem) => recorded.skipped.push(item),
        end: () => {
          recorded.ended = true;
        },
      } as unknown as vscode.TestRun;
      controller.createTestRun = () => fakeRun;
      return recorded;
    }

    function addScenario(controller: vscode.TestController, uri: vscode.Uri, line: number): vscode.TestItem {
      const item = controller.createTestItem(`${uri.toString()}#${line}`, 'S', uri);
      item.range = new vscode.Range(line, 0, line, 10);
      controller.items.add(item);
      return item;
    }

    test('marks a scenario passed when dotnet test reports no failures', async () => {
      const controller = createController();
      try {
        const uri = vscode.Uri.parse('file:///workspace/F.feature');
        const item = addScenario(controller, uri, 1);
        const recorded = withRecordingRun(controller);
        const client = fakeClient(() => Promise.resolve({ targets: [target()] }));
        const projectManager = fakeProjectManager([vscode.Uri.parse('file:///workspace/F.csproj').fsPath]);
        const runResult: DotnetTestRunResult = {
          results: [{ testName: 'AddNumbers', outcome: 'Passed', stdOut: 'done' }],
        };

        await runHandler(
          controller,
          client,
          projectManager,
          requestFor([item]),
          noToken,
          () => Promise.resolve(runResult),
        );

        assert.strictEqual(recorded.passed.length, 1);
        assert.strictEqual(recorded.passed[0], item);
        assert.strictEqual(recorded.failed.length, 0);
        assert.ok(recorded.ended);
      } finally {
        controller.dispose();
      }
    });

    test('marks a scenario failed and attaches the failed-step message', async () => {
      const controller = createController();
      try {
        const uri = vscode.Uri.parse('file:///workspace/F.feature');
        const item = addScenario(controller, uri, 1);
        const recorded = withRecordingRun(controller);
        const client = fakeClient(() => Promise.resolve({ targets: [target()] }));
        const projectManager = fakeProjectManager([vscode.Uri.parse('file:///workspace/F.csproj').fsPath]);
        const stdOut = ['Given a passing step', '-> done: M1() (0.0s)', 'When a failing step', '-> error: boom (0.0s)'].join('\n');
        const runResult: DotnetTestRunResult = {
          results: [{ testName: 'AddNumbers', outcome: 'Failed', stdOut, errorMessage: 'boom' }],
        };

        await runHandler(
          controller,
          client,
          projectManager,
          requestFor([item]),
          noToken,
          () => Promise.resolve(runResult),
        );

        assert.strictEqual(recorded.failed.length, 1);
        assert.strictEqual(recorded.failed[0].item, item);
        // parseStepTrace's detail captures everything after "error:", including the duration —
        // matches trxParser.test.ts's own expectations for the same stdout shape.
        assert.strictEqual(recorded.failed[0].message.message, 'boom (0.0s)');
      } finally {
        controller.dispose();
      }
    });

    test('skips a scenario with no resolved test target', async () => {
      const controller = createController();
      try {
        const uri = vscode.Uri.parse('file:///workspace/F.feature');
        const item = addScenario(controller, uri, 1);
        const recorded = withRecordingRun(controller);
        const client = fakeClient(() => Promise.resolve({ targets: [] }));
        const projectManager = fakeProjectManager([vscode.Uri.parse('file:///workspace/F.csproj').fsPath]);

        await runHandler(
          controller,
          client,
          projectManager,
          requestFor([item]),
          noToken,
          () => Promise.resolve({ results: [] }),
        );

        assert.strictEqual(recorded.skipped.length, 1);
        assert.strictEqual(recorded.passed.length, 0);
        assert.strictEqual(recorded.failed.length, 0);
      } finally {
        controller.dispose();
      }
    });

    test('errors a scenario when no owning project is found', async () => {
      const controller = createController();
      try {
        const uri = vscode.Uri.parse('file:///workspace/F.feature');
        const item = addScenario(controller, uri, 1);
        const recorded = withRecordingRun(controller);
        const client = fakeClient(() => Promise.resolve({ targets: [target()] }));
        const projectManager = fakeProjectManager([]);

        await runHandler(
          controller,
          client,
          projectManager,
          requestFor([item]),
          noToken,
          () => Promise.resolve({ results: [] }),
        );

        assert.strictEqual(recorded.errored.length, 1);
        assert.ok(messageText(recorded.errored[0].message).includes('could not find the project'));
      } finally {
        controller.dispose();
      }
    });

    test('errors a scenario when the run itself fails to launch', async () => {
      const controller = createController();
      try {
        const uri = vscode.Uri.parse('file:///workspace/F.feature');
        const item = addScenario(controller, uri, 1);
        const recorded = withRecordingRun(controller);
        const client = fakeClient(() => Promise.resolve({ targets: [target()] }));
        const projectManager = fakeProjectManager([vscode.Uri.parse('file:///workspace/F.csproj').fsPath]);

        await runHandler(
          controller,
          client,
          projectManager,
          requestFor([item]),
          noToken,
          () => Promise.resolve(null),
        );

        assert.strictEqual(recorded.errored.length, 1);
        assert.ok(messageText(recorded.errored[0].message).includes('failed to run'));
      } finally {
        controller.dispose();
      }
    });

    test('expands a file-level container item into its scenario children before running', async () => {
      const controller = createController();
      try {
        const uri = vscode.Uri.parse('file:///workspace/F.feature');
        const fileItem = ensureFileItem(controller, uri);
        const recorded = withRecordingRun(controller);
        const client = fakeClient(() => Promise.resolve({ targets: [target()] }));
        const projectManager = fakeProjectManager([vscode.Uri.parse('file:///workspace/F.csproj').fsPath]);
        const runResult: DotnetTestRunResult = {
          results: [{ testName: 'AddNumbers', outcome: 'Passed', stdOut: 'done' }],
        };

        await withStubbedExecuteCommand(
          () => Promise.resolve([methodSymbol('S', 1)]),
          () =>
            runHandler(
              controller,
              client,
              projectManager,
              requestFor([fileItem]),
              noToken,
              () => Promise.resolve(runResult),
            ),
        );

        assert.strictEqual(recorded.passed.length, 1);
      } finally {
        controller.dispose();
      }
    });
  });
});
