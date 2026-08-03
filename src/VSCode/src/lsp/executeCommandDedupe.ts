import { CancellationToken, Middleware, RegistrationParams } from 'vscode-languageclient/node';

/**
 * Middleware that drops `workspace/executeCommand` entries from an incoming
 * `client/registerCapability` batch when the command they'd register is one this extension
 * already registers itself at activation (see `extension.ts`'s `vscode.commands.registerCommand`
 * calls, e.g. `reqnroll.toggleComment` for the Ctrl+/ keybinding).
 *
 * Why this exists (issue #415): `vscode-languageclient`'s `ExecuteCommandFeature` responds to a
 * dynamic `workspace/executeCommand` registration by calling `vscode.commands.registerCommand`
 * for each advertised command name -- which throws ("command '...' already exists") when that id
 * is already taken. `client/registerCapability` bundles *every* dynamic registration for the
 * session into one array processed serially by `doRegisterCapability`
 * (`node_modules/vscode-languageclient/lib/common/client.js`), and that loop aborts entirely on
 * the first entry that throws. Because the array's order is effectively non-deterministic
 * (driven by the server's own collection ordering), an unrelated registration ordered after the
 * failing one -- e.g. `textDocument/onTypeFormatting` -- silently never registers.
 *
 * None of the affected commands actually need the generic dispatch this registration exists for:
 * this extension always invokes them via `client.sendRequest(ExecuteCommandRequest.type, ...)`
 * directly (see `commentToggle.ts`), never through `vscode.commands.executeCommand`. So the
 * client-registered command is strictly redundant, and dropping just that one registration entry
 * (not the whole batch) is safe -- Visual Studio and Rider are unaffected, since neither routes
 * dynamic registration through this generic `vscode-languageclient` machinery.
 */
export function createExecuteCommandDedupeMiddleware(
  ownedCommandIds: readonly string[],
): Middleware {
  const owned = new Set(ownedCommandIds);

  return {
    handleRegisterCapability: (params, next) => {
      const filtered: RegistrationParams = {
        registrations: params.registrations.filter((registration) => {
          if (registration.method !== 'workspace/executeCommand') return true;
          const commands: unknown = (
            registration.registerOptions as { commands?: unknown } | undefined
          )?.commands;
          if (!Array.isArray(commands)) return true;
          // Drop the whole registration only when EVERY command it advertises is already
          // client-owned -- a registration mixing owned and unowned commands is left intact
          // (VS Code has no API to register a subset of a single registration's commands).
          return !commands.every((c) => typeof c === 'string' && owned.has(c));
        }),
      };
      return Promise.resolve(next(filtered, CancellationToken.None)).then(() => undefined);
    },
  };
}
