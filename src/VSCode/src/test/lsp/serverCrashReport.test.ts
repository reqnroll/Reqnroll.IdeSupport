import * as assert from 'assert';
import {
  CrashReportCollector,
  crashReportProperties,
  formatProductFrame,
  parseCrashReport,
} from '../../lsp/serverCrashReport';

// What the .NET 10 runtime printed to stderr for each kind of crash (the same samples as CrashReportParserTests
// in Reqnroll.IdeSupport.Common.Tests, which must agree with this port), with the source paths shortened.
const NESTED = String.raw`Unhandled exception. System.InvalidOperationException: Outer failed for 'MyStep'
 ---> System.ApplicationException: middle
second line
 ---> System.IO.DirectoryNotFoundException: Could not find a part of the path 'C:\nope\missing.feature'.
   at Microsoft.Win32.SafeHandles.SafeFileHandle.CreateFile(String fullPath, FileMode mode, FileAccess access, FileShare share, FileOptions options)
   at System.IO.File.ReadAllText(String path, Encoding encoding)
   at Reqnroll.IdeSupport.CrashGen.Worker.Inner() in C:\Users\me\src\crashgen\Program.cs:line 31
   --- End of inner exception stack trace ---
   at Reqnroll.IdeSupport.CrashGen.Worker.Inner() in C:\Users\me\src\crashgen\Program.cs:line 32
   at Reqnroll.IdeSupport.CrashGen.Worker.Run() in C:\Users\me\src\crashgen\Program.cs:line 25
   --- End of inner exception stack trace ---
   at Reqnroll.IdeSupport.CrashGen.Worker.Run() in C:\Users\me\src\crashgen\Program.cs:line 26
   at Reqnroll.IdeSupport.CrashGen.Program.Main(String[] args) in C:\Users\me\src\crashgen\Program.cs:line 9
   at Reqnroll.IdeSupport.CrashGen.Program.<Main>(String[] args)`;

const LAMBDA = String.raw`Unhandled exception. System.NullReferenceException: Object reference not set to an instance of an object.
   at Reqnroll.IdeSupport.CrashGen.Worker.<>c.<Lambda>b__4_0() in C:\Users\me\src\crashgen\Program.cs:line 45
   at Reqnroll.IdeSupport.CrashGen.Worker.Lambda() in C:\Users\me\src\crashgen\Program.cs:line 46
   at System.Threading.Thread.StartCallback()`;

const STACK_OVERFLOW = String.raw`Stack overflow.
Repeated 24085 times:
--------------------------------
   at Reqnroll.IdeSupport.CrashGen.Program.Recurse(Int32)
--------------------------------
   at Reqnroll.IdeSupport.CrashGen.Program+<Main>d__0.MoveNext()
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[[System.__Canon, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]](System.__Canon ByRef)
   at Reqnroll.IdeSupport.CrashGen.Program.Main(System.String[])`;

const FAIL_FAST = String.raw`Process terminated.
Giving up on "C:\Users\me\secret.feature"
   at System.Environment.FailFast(System.String)
   at Reqnroll.IdeSupport.CrashGen.Program+<Main>d__0.MoveNext()`;

const lines = (text: string): string[] => text.split(/\r?\n/);

suite('serverCrashReport', () => {
  test('nested exceptions report the outer and the innermost exception', () => {
    assert.deepStrictEqual(crashReportProperties(parseCrashReport(lines(NESTED))!), {
      CrashKind: 'UnhandledException',
      ExceptionType: 'System.InvalidOperationException',
      Message: 'Outer failed for <text>',
      StackFrames:
        'Reqnroll.IdeSupport.CrashGen.Worker.Run:26\n' +
        'Reqnroll.IdeSupport.CrashGen.Program.Main:9\n' +
        'Reqnroll.IdeSupport.CrashGen.Program.Main',
      InnerExceptionCount: 2,
      InnerExceptionType: 'System.IO.DirectoryNotFoundException',
      InnerMessage: 'Could not find a part of the path <text>.',
      InnerStackFrames: '[external]\nReqnroll.IdeSupport.CrashGen.Worker.Inner:31',
    });
  });

  test('compiler-generated lambda frames are normalised', () => {
    assert.strictEqual(
      parseCrashReport(lines(LAMBDA))!.outer.stackFrames,
      'Reqnroll.IdeSupport.CrashGen.Worker.Lambda{lambda}:45\nReqnroll.IdeSupport.CrashGen.Worker.Lambda:46\n[external]',
    );
  });

  test('a stack overflow reports frames without an exception', () => {
    assert.deepStrictEqual(crashReportProperties(parseCrashReport(lines(STACK_OVERFLOW))!), {
      CrashKind: 'StackOverflow',
      StackFrames:
        'Reqnroll.IdeSupport.CrashGen.Program.Recurse\n' +
        'Reqnroll.IdeSupport.CrashGen.Program.Main\n' +
        '[external]\n' +
        'Reqnroll.IdeSupport.CrashGen.Program.Main',
    });
  });

  test('FailFast reports its scrubbed message', () => {
    assert.deepStrictEqual(crashReportProperties(parseCrashReport(lines(FAIL_FAST))!), {
      CrashKind: 'FailFast',
      Message: 'Giving up on <text>',
      StackFrames: '[external]\nReqnroll.IdeSupport.CrashGen.Program.Main',
    });
  });

  test('text without a crash header has no report', () => {
    assert.strictEqual(parseCrashReport(['just a warning', '']), undefined);
  });

  test('a lookalike namespace is not a product frame', () => {
    assert.strictEqual(formatProductFrame('Reqnroll.IdeSupportLookalike.Evil.Run', 1), undefined);
  });

  test('constructors, nested and generic types are normalised like the C# parser', () => {
    assert.strictEqual(
      formatProductFrame('Reqnroll.IdeSupport.X.Worker..ctor', 3),
      'Reqnroll.IdeSupport.X.Worker.{ctor}:3',
    );
    assert.strictEqual(
      formatProductFrame('Reqnroll.IdeSupport.X.Worker..cctor', 0),
      'Reqnroll.IdeSupport.X.Worker.{cctor}',
    );
    assert.strictEqual(
      formatProductFrame('Reqnroll.IdeSupport.X.Outer+Inner.Run', 0),
      'Reqnroll.IdeSupport.X.Outer.Inner.Run',
    );
    assert.strictEqual(
      formatProductFrame('Reqnroll.IdeSupport.X.Box`1.Get', 0),
      'Reqnroll.IdeSupport.X.Box.Get',
    );
  });

  test('the collector keeps only lines from the last header, bounded', () => {
    const collector = new CrashReportCollector();
    collector.onLine('warn: unrelated');
    assert.strictEqual(collector.parse(), undefined);

    collector.onLine('Unhandled exception. System.Exception: ' + 'x'.repeat(10_000));
    for (let i = 0; i < CrashReportCollector.maxLines + 50; i++) {
      collector.onLine(`   at Reqnroll.IdeSupport.X.Y.Run() in C:\\x.cs:line ${i + 1}`);
    }
    const report = collector.parse()!;
    assert.ok(report.outer.message!.length <= 128);
    assert.ok(report.outer.stackFrames!.startsWith('Reqnroll.IdeSupport.X.Y.Run:1\n'));
  });
});
