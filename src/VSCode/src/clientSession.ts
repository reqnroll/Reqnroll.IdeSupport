import { randomUUID } from 'crypto';

/**
 * The id of this VS Code session as the extension sees it (issue #1199): one random GUID per extension-host
 * process. It is stamped as `ClientSessionId` on every event this extension transmits and passed to the
 * language server as `--client-session-id`, which stamps it on the server's events too, so a client-originated
 * `UnhandledException` joins to `ServerSessionStarted`. Mirrors `ClientSession` in `Reqnroll.IdeSupport.Common`
 * (and the Rider plugin's `ClientSession`). Random, not user-identifying.
 */
export const CLIENT_SESSION_ID = randomUUID();

/** Telemetry property name; mirrors `ClientSession.PropertyName`. */
export const CLIENT_SESSION_ID_PROPERTY = 'ClientSessionId';

/** Server command-line flag; mirrors `ClientSession.ServerArgumentName`. */
export const CLIENT_SESSION_ID_ARGUMENT = '--client-session-id';
