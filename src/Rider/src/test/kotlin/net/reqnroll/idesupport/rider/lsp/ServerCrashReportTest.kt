package net.reqnroll.idesupport.rider.lsp

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

// What the .NET 10 runtime printed to stderr for each kind of crash (the same samples as CrashReportParserTests
// in Reqnroll.IdeSupport.Common.Tests and serverCrashReport.test.ts, which must agree), with paths shortened.
class ServerCrashReportTest {
    private val nested = """
        Unhandled exception. System.InvalidOperationException: Outer failed for 'MyStep'
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
           at Reqnroll.IdeSupport.CrashGen.Program.<Main>(String[] args)
    """.trimIndent()

    private val lambda = """
        Unhandled exception. System.NullReferenceException: Object reference not set to an instance of an object.
           at Reqnroll.IdeSupport.CrashGen.Worker.<>c.<Lambda>b__4_0() in C:\Users\me\src\crashgen\Program.cs:line 45
           at Reqnroll.IdeSupport.CrashGen.Worker.Lambda() in C:\Users\me\src\crashgen\Program.cs:line 46
           at System.Threading.Thread.StartCallback()
    """.trimIndent()

    private val stackOverflow = """
        Stack overflow.
        Repeated 24085 times:
        --------------------------------
           at Reqnroll.IdeSupport.CrashGen.Program.Recurse(Int32)
        --------------------------------
           at Reqnroll.IdeSupport.CrashGen.Program+<Main>d__0.MoveNext()
           at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[[System.__Canon, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]](System.__Canon ByRef)
           at Reqnroll.IdeSupport.CrashGen.Program.Main(System.String[])
    """.trimIndent()

    private val failFast = """
        Process terminated.
        Giving up on "C:\Users\me\secret.feature"
           at System.Environment.FailFast(System.String)
           at Reqnroll.IdeSupport.CrashGen.Program+<Main>d__0.MoveNext()
    """.trimIndent()

    private fun parse(text: String) = ServerCrashReportParser.parse(text.lines())

    @Test
    fun `nested exceptions report the outer and the innermost exception`() {
        assertEquals(
            mapOf<String, Any?>(
                "CrashKind" to "UnhandledException",
                "ExceptionType" to "System.InvalidOperationException",
                "Message" to "Outer failed for <text>",
                "StackFrames" to "Reqnroll.IdeSupport.CrashGen.Worker.Run:26\n" +
                    "Reqnroll.IdeSupport.CrashGen.Program.Main:9\n" +
                    "Reqnroll.IdeSupport.CrashGen.Program.Main",
                "InnerExceptionCount" to 2,
                "InnerExceptionType" to "System.IO.DirectoryNotFoundException",
                "InnerMessage" to "Could not find a part of the path <text>.",
                "InnerStackFrames" to "[external]\nReqnroll.IdeSupport.CrashGen.Worker.Inner:31",
            ),
            parse(nested)!!.toProperties(),
        )
    }

    @Test
    fun `compiler-generated lambda frames are normalised`() {
        assertEquals(
            "Reqnroll.IdeSupport.CrashGen.Worker.Lambda{lambda}:45\nReqnroll.IdeSupport.CrashGen.Worker.Lambda:46\n[external]",
            parse(lambda)!!.outer.stackFrames,
        )
    }

    @Test
    fun `a stack overflow reports frames without an exception`() {
        assertEquals(
            mapOf<String, Any?>(
                "CrashKind" to "StackOverflow",
                "StackFrames" to "Reqnroll.IdeSupport.CrashGen.Program.Recurse\n" +
                    "Reqnroll.IdeSupport.CrashGen.Program.Main\n" +
                    "[external]\n" +
                    "Reqnroll.IdeSupport.CrashGen.Program.Main",
            ),
            parse(stackOverflow)!!.toProperties(),
        )
    }

    @Test
    fun `FailFast reports its scrubbed message`() {
        assertEquals(
            mapOf<String, Any?>(
                "CrashKind" to "FailFast",
                "Message" to "Giving up on <text>",
                "StackFrames" to "[external]\nReqnroll.IdeSupport.CrashGen.Program.Main",
            ),
            parse(failFast)!!.toProperties(),
        )
    }

    @Test
    fun `text without a crash header has no report`() {
        assertNull(ServerCrashReportParser.parse(listOf("just a warning", "")))
    }

    @Test
    fun `constructors, nested and generic types are normalised like the C# parser`() {
        assertEquals("Reqnroll.IdeSupport.X.Worker.{ctor}:3", ServerCrashReportParser.formatProductFrame("Reqnroll.IdeSupport.X.Worker..ctor", 3))
        assertEquals("Reqnroll.IdeSupport.X.Worker.{cctor}", ServerCrashReportParser.formatProductFrame("Reqnroll.IdeSupport.X.Worker..cctor", 0))
        assertEquals("Reqnroll.IdeSupport.X.Outer.Inner.Run", ServerCrashReportParser.formatProductFrame("Reqnroll.IdeSupport.X.Outer+Inner.Run", 0))
        assertEquals("Reqnroll.IdeSupport.X.Box.Get", ServerCrashReportParser.formatProductFrame("Reqnroll.IdeSupport.X.Box`1.Get", 0))
        assertNull(ServerCrashReportParser.formatProductFrame("Reqnroll.IdeSupportLookalike.Evil.Run", 1))
    }

    @Test
    fun `the collector joins chunks into lines and keeps only the last report, bounded`() {
        val collector = ServerCrashReportCollector()
        collector.onText("warn: unrelated\nUnhandled exception. System.Exception: ")
        collector.onText("x".repeat(10_000) + "\r\n")
        repeat(ServerCrashReportCollector.MAX_LINES + 50) {
            collector.onText("   at Reqnroll.IdeSupport.X.Y.Run() in C:\\x.cs:line ${it + 1}\n")
        }

        val report = collector.parse()!!

        assertEquals("System.Exception", report.outer.type)
        assertTrue(report.outer.message!!.length <= 128)
        assertTrue(report.outer.stackFrames!!.startsWith("Reqnroll.IdeSupport.X.Y.Run:1\n"))
    }

    @Test
    fun `a final line without a break is still parsed`() {
        val collector = ServerCrashReportCollector()
        collector.onText("Stack overflow.\n   at Reqnroll.IdeSupport.X.Y.Recurse(Int32)")

        assertEquals("Reqnroll.IdeSupport.X.Y.Recurse", collector.parse()!!.outer.stackFrames)
    }
}
