import * as assert from 'assert';
import { CancellationToken, RegistrationParams } from 'vscode-languageclient/node';
import { createExecuteCommandDedupeMiddleware } from '../../lsp/executeCommandDedupe';

function registrations(regs: RegistrationParams['registrations']): RegistrationParams {
  return { registrations: regs };
}

/** Invokes `handleRegisterCapability` and captures whatever it forwards to `next`. */
async function forward(
  middleware: ReturnType<typeof createExecuteCommandDedupeMiddleware>,
  params: RegistrationParams,
): Promise<RegistrationParams | undefined> {
  let forwarded: RegistrationParams | undefined;
  await middleware.handleRegisterCapability!(
    params,
    (p: RegistrationParams, _token: CancellationToken) => {
      forwarded = p;
      return undefined;
    },
  );
  return forwarded;
}

suite('executeCommandDedupe', () => {
  suite('createExecuteCommandDedupeMiddleware', () => {
    test('drops a workspace/executeCommand registration whose commands are all client-owned', async () => {
      const middleware = createExecuteCommandDedupeMiddleware(['reqnroll.toggleComment']);
      const forwarded = await forward(
        middleware,
        registrations([
          {
            id: '1',
            method: 'workspace/executeCommand',
            registerOptions: { commands: ['reqnroll.toggleComment'] },
          },
          { id: '2', method: 'textDocument/onTypeFormatting', registerOptions: {} },
        ]),
      );

      assert.strictEqual(forwarded?.registrations.length, 1);
      assert.strictEqual(forwarded?.registrations[0].method, 'textDocument/onTypeFormatting');
    });

    test('keeps a mixed registration where only some of its commands are client-owned', async () => {
      const middleware = createExecuteCommandDedupeMiddleware(['reqnroll.toggleComment']);
      const forwarded = await forward(
        middleware,
        registrations([
          {
            id: '1',
            method: 'workspace/executeCommand',
            registerOptions: { commands: ['reqnroll.toggleComment', 'reqnroll.other'] },
          },
        ]),
      );

      assert.strictEqual(forwarded?.registrations.length, 1);
    });

    test('leaves registrations for other methods untouched', async () => {
      const middleware = createExecuteCommandDedupeMiddleware(['reqnroll.toggleComment']);
      const forwarded = await forward(
        middleware,
        registrations([{ id: '1', method: 'textDocument/codeLens', registerOptions: {} }]),
      );

      assert.strictEqual(forwarded?.registrations.length, 1);
    });

    test('leaves a workspace/executeCommand registration untouched when registerOptions.commands is not an array', async () => {
      const middleware = createExecuteCommandDedupeMiddleware(['reqnroll.toggleComment']);
      const forwarded = await forward(
        middleware,
        registrations([{ id: '1', method: 'workspace/executeCommand', registerOptions: {} }]),
      );

      assert.strictEqual(forwarded?.registrations.length, 1);
    });

    test('keeps a workspace/executeCommand registration when no owned command ids are configured', async () => {
      const middleware = createExecuteCommandDedupeMiddleware([]);
      const forwarded = await forward(
        middleware,
        registrations([
          {
            id: '1',
            method: 'workspace/executeCommand',
            registerOptions: { commands: ['reqnroll.toggleComment'] },
          },
        ]),
      );

      assert.strictEqual(forwarded?.registrations.length, 1);
    });
  });
});
