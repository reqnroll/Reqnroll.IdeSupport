import { scrubMessage } from '../clientExceptionTelemetry';
import { EXTERNAL_PLACEHOLDER, MAX_FRAMES, MAX_LENGTH } from '../stackSanitizer';

/**
 * Parses the crash report the .NET runtime writes to the language server's stderr as it dies (an unhandled
 * exception, a stack overflow, a fatal runtime error or `Environment.FailFast`) into telemetry-safe values:
 * full type names, scrubbed messages and product-only frames. The TypeScript counterpart of `CrashReportParser`
 * in `Reqnroll.IdeSupport.Common` and so the same property contract; keep the two in step.
 *
 * The unhandled-exception text is `Exception.ToString()`: the outer header, one ` ---> ` header per nested
 * exception, then the innermost exception's frames, an `--- End of inner exception stack trace ---` line,
 * the next one out's frames, and so on, ending with the outermost exception's frames.
 */

/** Closed set of `CrashKind` values; mirrors `CrashKind` in `CrashReport.cs`. */
export const CrashKind = {
  unhandledException: 'UnhandledException',
  stackOverflow: 'StackOverflow',
  fatalError: 'FatalError',
  failFast: 'FailFast',
} as const;

/** Property keys; mirror the `CrashReport` key constants in `CrashReport.cs`. */
export const CrashReportProperties = {
  crashKind: 'CrashKind',
  exceptionType: 'ExceptionType',
  message: 'Message',
  stackFrames: 'StackFrames',
  innerExceptionType: 'InnerExceptionType',
  innerMessage: 'InnerMessage',
  innerStackFrames: 'InnerStackFrames',
  innerExceptionCount: 'InnerExceptionCount',
} as const;

/** One exception of a crash report; any member may be missing. */
export interface CrashedException {
  type?: string;
  message?: string;
  stackFrames?: string;
}

export interface CrashReport {
  kind: string;
  outer: CrashedException;
  inner?: CrashedException;
  innerExceptionCount: number;
}

const MAX_MESSAGE_LENGTH = 128;
const PRODUCT_PREFIX = 'Reqnroll.IdeSupport.';
const END_OF_INNER_STACK = '--- End of inner exception stack trace ---';
const INNER_PREFIX = '--->';

const HEADERS: ReadonlyArray<[string, string]> = [
  ['Unhandled exception.', CrashKind.unhandledException],
  ['Stack overflow.', CrashKind.stackOverflow],
  ['Fatal error.', CrashKind.fatalError],
  ['Process terminated.', CrashKind.failFast],
];

// Generic arguments print in brackets after the method name, as Method[T] or, in a stack-overflow or FailFast
// report, Method[[System.__Canon, System.Private.CoreLib, ...]], which contains spaces.
const FRAME = /^\s*at ([^\s([]+)(?:\[[^(]*\])?\([^)]*\)(?: in .*:line (\d+))?/;
const INNER_ORDINAL = /^\(Inner Exception #\d+\)\s*/;
const TYPE_NAME = /^[A-Za-z_][A-Za-z0-9_.`+]*$/;

function headerOf(line: string): [string, string] | undefined {
  const trimmed = line.trimStart();
  return HEADERS.find(([prefix]) => trimmed.startsWith(prefix));
}

/** Whether `line` starts one of the runtime's crash reports. */
export function isCrashHeader(line: string): boolean {
  return headerOf(line) !== undefined;
}

function identifier(name: string): string {
  const cut = name.search(/[<(]/);
  return (cut >= 0 ? name.slice(0, cut) : name).replace(/[^A-Za-z0-9_]/g, '');
}

// "<Foo>b__0_1" -> "Foo{lambda}"; "<<Foo>b__0>g__Local|1_0" -> "Foo{local}"; "<Foo>d__3" -> "Foo"
function fromCompilerName(compilerName: string): string {
  let start = 0;
  while (start < compilerName.length && compilerName[start] === '<') start++;
  const end = compilerName.indexOf('>', start);
  const inner = end > start ? identifier(compilerName.slice(start, end)) : '';
  if (inner.length === 0) return '{anonymous}';
  if (compilerName.includes('>g__')) return `${inner}{local}`;
  if (compilerName.includes('>b__')) return `${inner}{lambda}`;
  return inner;
}

function normalizeMethodName(methodName: string, declaringTypeName: string): string {
  if (methodName.startsWith('<')) return fromCompilerName(methodName);
  if (declaringTypeName.startsWith('<')) {
    const fromType = fromCompilerName(declaringTypeName);
    if (fromType !== '{anonymous}') return fromType;
  }
  if (methodName === '.ctor') return '{ctor}';
  if (methodName === '.cctor') return '{cctor}';
  return identifier(methodName);
}

/** Formats one text frame like the server's `ExceptionStackSanitizer.FormatProductFrameText`; `undefined` for a non-product frame. */
export function formatProductFrame(qualifiedName: string, line: number): string | undefined {
  if (!qualifiedName.startsWith(PRODUCT_PREFIX)) return undefined;
  // ".ctor"/".cctor" contain the separator themselves; keep them together.
  const ctor = /\.(\.c?ctor)$/.exec(qualifiedName);
  const head = ctor ? qualifiedName.slice(0, ctor.index) : qualifiedName;
  const segments = head.split(/[.+]/);
  if (ctor) segments.push(ctor[1]);
  if (segments.length < 2) return undefined;

  const method = segments[segments.length - 1];
  const declaringType = segments[segments.length - 2];
  const kept = segments
    .slice(0, -1)
    .filter((s) => s.length > 0 && !s.startsWith('<'))
    .map((s) => identifier(s.replace(/`\d+$/, '')));
  kept.push(normalizeMethodName(method, declaringType));
  return kept.join('.') + (line > 0 ? `:${line}` : '');
}

/** Joins formatted frames (undefined = non-product) under the shared `StackFrames` contract. */
export function joinFrames(frames: ReadonlyArray<string | undefined>): string | undefined {
  const entries: string[] = [];
  let sawProduct = false;
  for (const name of frames) {
    if (entries.length >= MAX_FRAMES) break;
    if (name === undefined) {
      if (entries.length === 0 || entries[entries.length - 1] !== EXTERNAL_PLACEHOLDER) {
        entries.push(EXTERNAL_PLACEHOLDER);
      }
    } else {
      sawProduct = true;
      entries.push(name);
    }
  }
  if (!sawProduct) return undefined;

  let result = '';
  for (const entry of entries) {
    const added = (result.length === 0 ? 0 : 1) + entry.length;
    if (result.length + added > MAX_LENGTH) break;
    result += (result.length === 0 ? '' : '\n') + entry;
  }
  return result.length === 0 ? undefined : result;
}

function scrub(text: string | undefined): string | undefined {
  const trimmed = text?.trim();
  if (!trimmed) return undefined;
  return scrubMessage(trimmed).slice(0, MAX_MESSAGE_LENGTH);
}

// "Namespace.TypeName: message" -> type and scrubbed message; a header that does not start with a type name
// keeps no type, and its text is treated as the message.
function toException(header: string, stackFrames: string | undefined): CrashedException {
  const separator = header.indexOf(': ');
  const candidate = separator > 0 ? header.slice(0, separator) : header;
  if (!TYPE_NAME.test(candidate)) return { message: scrub(header), stackFrames };
  return {
    type: candidate,
    message: separator > 0 ? scrub(header.slice(separator + 2)) : undefined,
    stackFrames,
  };
}

/** Parses the last crash report in `lines`; `undefined` when there is none. Never throws. */
export function parseCrashReport(lines: ReadonlyArray<string>): CrashReport | undefined {
  try {
    return parse(lines);
  } catch {
    return undefined;
  }
}

function parse(lines: ReadonlyArray<string>): CrashReport | undefined {
  let start = -1;
  for (let i = lines.length - 1; i >= 0; i--) {
    if (isCrashHeader(lines[i])) {
      start = i;
      break;
    }
  }
  if (start < 0) return undefined;

  const headerLine = lines[start].trimStart();
  const [prefix, kind] = headerOf(headerLine)!;
  const headers: string[] =
    kind === CrashKind.stackOverflow ? [] : [headerLine.slice(prefix.length).trim()];
  const sections: Array<Array<string | undefined>> = [[]];
  let inFrames = false;

  for (let i = start + 1; i < lines.length; i++) {
    const line = lines[i] ?? '';
    const trimmed = line.trim();
    const frame = FRAME.exec(line);
    if (frame) {
      inFrames = true;
      const formatted = formatProductFrame(frame[1], frame[2] ? Number(frame[2]) : 0);
      const section = sections[sections.length - 1];
      // A stack overflow prints its repeating block once, but consecutive repeats of one frame add nothing.
      if (section.length === 0 || section[section.length - 1] !== formatted)
        section.push(formatted);
    } else if (trimmed === END_OF_INNER_STACK) {
      sections.push([]);
    } else if (!inFrames && kind !== CrashKind.stackOverflow && trimmed.startsWith(INNER_PREFIX)) {
      headers.push(trimmed.slice(INNER_PREFIX.length).trim().replace(INNER_ORDINAL, ''));
    } else if (!inFrames && headers.length > 0 && trimmed.length > 0) {
      // A message spanning several lines continues the latest header.
      headers[headers.length - 1] += ` ${trimmed}`;
    }
  }

  const outerFrames = joinFrames(sections[sections.length - 1]);
  if (kind === CrashKind.stackOverflow) {
    return { kind, outer: { stackFrames: outerFrames }, innerExceptionCount: 0 };
  }

  const outer =
    kind === CrashKind.failFast
      ? { message: scrub(headers[0]), stackFrames: outerFrames }
      : toException(headers[0], outerFrames);
  if (headers.length < 2) return { kind, outer, innerExceptionCount: 0 };

  const innerFrames = sections.length > 1 ? joinFrames(sections[0]) : undefined;
  return {
    kind,
    outer,
    inner: toException(headers[headers.length - 1], innerFrames),
    innerExceptionCount: headers.length - 1,
  };
}

/** The report as event properties; unknown values are omitted. */
export function crashReportProperties(report: CrashReport): Record<string, string | number> {
  const properties: Record<string, string | number> = {
    [CrashReportProperties.crashKind]: report.kind,
  };
  const add = (key: string, value: string | undefined): void => {
    if (value) properties[key] = value;
  };
  add(CrashReportProperties.exceptionType, report.outer.type);
  add(CrashReportProperties.message, report.outer.message);
  add(CrashReportProperties.stackFrames, report.outer.stackFrames);
  if (report.inner) {
    properties[CrashReportProperties.innerExceptionCount] = report.innerExceptionCount;
    add(CrashReportProperties.innerExceptionType, report.inner.type);
    add(CrashReportProperties.innerMessage, report.inner.message);
    add(CrashReportProperties.innerStackFrames, report.inner.stackFrames);
  }
  return properties;
}

/**
 * Collects stderr lines from the last crash-report header onwards (bounded), so the report can be parsed once
 * the server has died. Lines before any header are not kept.
 */
export class CrashReportCollector {
  static readonly maxLines = 400;
  static readonly maxLineLength = 2048;

  private _lines: string[] = [];
  private _capturing = false;

  onLine(line: string): void {
    if (isCrashHeader(line)) {
      this._lines = [];
      this._capturing = true;
    }
    if (this._capturing && this._lines.length < CrashReportCollector.maxLines) {
      this._lines.push(line.slice(0, CrashReportCollector.maxLineLength));
    }
  }

  /** The crash report collected so far, or `undefined` when no header was seen. */
  parse(): CrashReport | undefined {
    return this._capturing ? parseCrashReport(this._lines) : undefined;
  }
}
