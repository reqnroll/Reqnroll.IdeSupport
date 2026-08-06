import * as assert from 'assert';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import {
  buildRunLens,
  collectMethodSymbols,
  registerRunCodeLens,
} from '../../commands/runCodeLens';
import { TestResultStore } from '../../testRunner/testResultStore';
import { ScenarioTestTargetDto } from '../../testRunner/scenarioTestTarget';

function fakeClient(overrides: {
  sendRequest?: (method: string, params: unknown) => Promise<unknown>;
}): LanguageClient {
  return {
    sendRequest: overrides.sendRequest ?? (() => Promise.resolve({ targets: [] })),
    onRequest: () => ({ dispose: () => undefined }),
  } as unknown as LanguageClient;
}

function fakeContext(): vscode.ExtensionContext {
  return { subscriptions: [] } as unknown as vscode.ExtensionContext;
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

/** Registers the provider, capturing it the same way `stepCodeLens.test.ts` does. */
function captureProvider(
  client: LanguageClient,
  resultStore: TestResultStore,
): vscode.CodeLensProvider {
  const original = vscode.languages.registerCodeLensProvider;
  let captured: vscode.CodeLensProvider | undefined;
  (vscode.languages as unknown as { registerCodeLensProvider: unknown }).registerCodeLensProvider =
    (_selector: unknown, provider: vscode.CodeLensProvider) => {
      captured = provider;
      return { dispose: () => undefined };
    };

  try {
    registerRunCodeLens(client, fakeContext(), resultStore);
  } finally {
    (
      vscode.languages as unknown as { registerCodeLensProvider: unknown }
    ).registerCodeLensProvider = original;
  }

  assert.ok(captured, 'registerCodeLensProvider should have been called');
  return captured;
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

suite('runCodeLens', () => {
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

  suite('buildRunLens', () => {
    const range = new vscode.Range(1, 0, 1, 10);

    test('returns undefined when there are no resolved targets', () => {
      const lens = buildRunLens('file:///F.feature', range, [], undefined);
      assert.strictEqual(lens, undefined);
    });

    test('uses the play icon when no cached result exists', () => {
      const lens = buildRunLens('file:///F.feature', range, [target()], undefined);
      assert.strictEqual(lens?.command?.title, '$(play) Run');
    });

    test('uses the check icon for a cached passing result', () => {
      const lens = buildRunLens('file:///F.feature', range, [target()], 'passed');
      assert.strictEqual(lens?.command?.title, '$(check) Run');
    });

    test('uses the error icon for a cached failing result', () => {
      const lens = buildRunLens('file:///F.feature', range, [target()], 'failed');
      assert.strictEqual(lens?.command?.title, '$(error) Run');
    });

    test('forwards the targets through the command arguments', () => {
      const targets = [target({ methodName: 'M1' }), target({ methodName: 'M2' })];
      const lens = buildRunLens('file:///F.feature', range, targets, undefined);
      const args = lens?.command?.arguments?.[0] as { targets: ScenarioTestTargetDto[] };
      assert.strictEqual(args.targets.length, 2);
      assert.strictEqual(args.targets[1].methodName, 'M2');
    });
  });

  suite('provideCodeLenses', () => {
    test('renders a lens for a scenario with resolved targets', async () => {
      const client = fakeClient({
        sendRequest: () => Promise.resolve({ targets: [target()] }),
      });
      const resultStore = new TestResultStore();
      const provider = captureProvider(client, resultStore);
      const document = { uri: vscode.Uri.parse('file:///F.feature') } as vscode.TextDocument;

      const lenses = await withStubbedExecuteCommand(
        () => Promise.resolve([methodSymbol('S', 1)]),
        () => Promise.resolve(provider.provideCodeLenses(document, {} as vscode.CancellationToken)),
      );

      assert.strictEqual(lenses?.length, 1);
      assert.strictEqual(lenses[0].command?.command, 'reqnroll.runTest');
    });

    test('renders no lens for a scenario whose targets resolve empty (not built yet)', async () => {
      const client = fakeClient({ sendRequest: () => Promise.resolve({ targets: [] }) });
      const resultStore = new TestResultStore();
      const provider = captureProvider(client, resultStore);
      const document = { uri: vscode.Uri.parse('file:///F.feature') } as vscode.TextDocument;

      const lenses = await withStubbedExecuteCommand(
        () => Promise.resolve([methodSymbol('S', 1)]),
        () => Promise.resolve(provider.provideCodeLenses(document, {} as vscode.CancellationToken)),
      );

      assert.deepStrictEqual(lenses, []);
    });

    test('returns an empty array when the document has no symbols', async () => {
      const client = fakeClient({});
      const resultStore = new TestResultStore();
      const provider = captureProvider(client, resultStore);
      const document = { uri: vscode.Uri.parse('file:///F.feature') } as vscode.TextDocument;

      const lenses = await withStubbedExecuteCommand(
        () => Promise.resolve(undefined),
        () => Promise.resolve(provider.provideCodeLenses(document, {} as vscode.CancellationToken)),
      );

      assert.deepStrictEqual(lenses, []);
    });

    test('finds scenarios nested under a Rule via executeDocumentSymbolProvider', async () => {
      const client = fakeClient({ sendRequest: () => Promise.resolve({ targets: [target()] }) });
      const resultStore = new TestResultStore();
      const provider = captureProvider(client, resultStore);
      const document = { uri: vscode.Uri.parse('file:///F.feature') } as vscode.TextDocument;

      const rule = namespaceSymbol('R', [methodSymbol('Nested', 3)]);
      const lenses = await withStubbedExecuteCommand(
        () => Promise.resolve([rule]),
        () => Promise.resolve(provider.provideCodeLenses(document, {} as vscode.CancellationToken)),
      );

      assert.strictEqual(lenses?.length, 1);
    });
  });
});
