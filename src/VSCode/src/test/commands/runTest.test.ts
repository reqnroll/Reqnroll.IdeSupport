import * as assert from 'assert';
import * as vscode from 'vscode';
import { doRunTest } from '../../commands/runTest';
import { RunTestCommandArgs } from '../../commands/runCodeLens';
import { ResultDecorationService } from '../../testRunner/resultDecorationService';
import { TestResultStore } from '../../testRunner/testResultStore';
import { DotnetTestRunResult } from '../../testRunner/dotnetTestRunner';
import { ScenarioTestTargetDto } from '../../testRunner/scenarioTestTarget';

function fakeProjectManager(knownProjects: readonly string[]) {
  return { getKnownProjects: () => new Set(knownProjects) };
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

function argsFor(uri: string, targets: ScenarioTestTargetDto[]): RunTestCommandArgs {
  return {
    uri,
    range: { start: { line: 1, character: 0 }, end: { line: 1, character: 20 } },
    targets,
  };
}

function withStubbedErrorMessage<T>(fn: () => Promise<T>): Promise<[T, string[]]> {
  const messages: string[] = [];
  const originalError = vscode.window.showErrorMessage;
  const originalWarn = vscode.window.showWarningMessage;
  (vscode.window as unknown as { showErrorMessage: unknown }).showErrorMessage = (msg: string) => {
    messages.push(msg);
    return Promise.resolve(undefined);
  };
  (vscode.window as unknown as { showWarningMessage: unknown }).showWarningMessage = (msg: string) => {
    messages.push(msg);
    return Promise.resolve(undefined);
  };
  return fn()
    .then((result): [T, string[]] => [result, messages])
    .finally(() => {
      (vscode.window as unknown as { showErrorMessage: unknown }).showErrorMessage = originalError;
      (vscode.window as unknown as { showWarningMessage: unknown }).showWarningMessage = originalWarn;
    });
}

suite('runTest', () => {
  suite('doRunTest', () => {
    test('shows an error when no owning project is found', async () => {
      const projectManager = fakeProjectManager([]);
      const resultStore = new TestResultStore();
      const decorationService = { applyResult: () => undefined } as unknown as ResultDecorationService;

      const [, messages] = await withStubbedErrorMessage(() =>
        doRunTest(
          projectManager,
          resultStore,
          decorationService,
          argsFor('file:///workspace/Foo.feature', [target()]),
          () => Promise.resolve(null),
        ),
      );

      assert.ok(messages.some((m) => m.includes('could not find the project')));
    });

    test('shows an error when the run itself fails (dotnet unavailable, timeout, etc.)', async () => {
      const projectFile = vscode.Uri.parse('file:///workspace/Foo.csproj').fsPath;
      const projectManager = fakeProjectManager([projectFile]);
      const resultStore = new TestResultStore();
      const decorationService = { applyResult: () => undefined } as unknown as ResultDecorationService;

      const [, messages] = await withStubbedErrorMessage(() =>
        doRunTest(
          projectManager,
          resultStore,
          decorationService,
          argsFor('file:///workspace/Foo.feature', [target()]),
          () => Promise.resolve(null),
        ),
      );

      assert.ok(messages.some((m) => m.includes('failed to run')));
    });

    test('updates the result store and applies decorations on a passing run', async () => {
      const projectFile = vscode.Uri.parse('file:///workspace/Foo.csproj').fsPath;
      const projectManager = fakeProjectManager([projectFile]);
      const resultStore = new TestResultStore();
      let appliedUri: string | undefined;
      const decorationService = {
        applyResult: (uri: string) => {
          appliedUri = uri;
        },
      } as unknown as ResultDecorationService;

      const runResult: DotnetTestRunResult = {
        results: [{ testName: 'AddNumbers', outcome: 'Passed', stdOut: 'Given ok\n-> done: M() (0.0s)' }],
      };

      const args = argsFor('file:///workspace/Foo.feature', [target()]);
      await doRunTest(projectManager, resultStore, decorationService, args, () => Promise.resolve(runResult));

      const stored = resultStore.get(args.uri, args.range.start.line);
      assert.strictEqual(stored?.outcome, 'passed');
      assert.strictEqual(appliedUri, args.uri);
    });

    test('marks the scenario failed and records the failed step from the stdout trace', async () => {
      const projectFile = vscode.Uri.parse('file:///workspace/Foo.csproj').fsPath;
      const projectManager = fakeProjectManager([projectFile]);
      const resultStore = new TestResultStore();
      const decorationService = { applyResult: () => undefined } as unknown as ResultDecorationService;

      const stdOut = [
        'Given a passing step',
        '-> done: M1() (0.0s)',
        'When a failing step',
        '-> error: boom (0.0s)',
      ].join('\n');
      const runResult: DotnetTestRunResult = {
        results: [{ testName: 'AddNumbers', outcome: 'Failed', stdOut, errorMessage: 'boom' }],
      };

      const args = argsFor('file:///workspace/Foo.feature', [target()]);
      await doRunTest(projectManager, resultStore, decorationService, args, () => Promise.resolve(runResult));

      const stored = resultStore.get(args.uri, args.range.start.line);
      assert.strictEqual(stored?.outcome, 'failed');
      assert.strictEqual(stored?.failedStep?.stepText, 'When a failing step');
    });

    test('any row failing in a row-tests result marks the scenario failed', async () => {
      const projectFile = vscode.Uri.parse('file:///workspace/Foo.csproj').fsPath;
      const projectManager = fakeProjectManager([projectFile]);
      const resultStore = new TestResultStore();
      const decorationService = { applyResult: () => undefined } as unknown as ResultDecorationService;

      const runResult: DotnetTestRunResult = {
        results: [
          { testName: 'AddNumbers (1)', outcome: 'Passed', stdOut: 'done' },
          { testName: 'AddNumbers (2)', outcome: 'Failed', stdOut: 'Given x\n-> error: boom (0.0s)' },
        ],
      };

      const args = argsFor('file:///workspace/Foo.feature', [target(), target({ rowIndex: 1, isParameterized: true })]);
      await doRunTest(projectManager, resultStore, decorationService, args, () => Promise.resolve(runResult));

      assert.strictEqual(resultStore.get(args.uri, args.range.start.line)?.outcome, 'failed');
    });
  });
});
