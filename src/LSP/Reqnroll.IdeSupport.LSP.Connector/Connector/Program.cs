using ReqnrollConnector;
using ReqnrollConnector.AssemblyLoading;
using ReqnrollConnector.Logging;

// FileLogger (issue #628) gives a Connector crash a durable artifact of its own, independent of
// whatever the LSP server managed to capture from this process's stdout/stderr — but only writes
// unconditionally when asked to: --file-log (issue #637), which OutProcReqnrollConnector adds
// whenever the server's own --log-level is Info or more verbose, so "turn up logging" means the
// same thing everywhere in this system. --debug also implies it — someone who wants a debugger
// attached almost certainly wants the log too — but --file-log is the primary, properly-wired
// mechanism; --debug alone would otherwise be the only lever, and it hangs the process waiting for
// a debugger to attach, which is the wrong trade-off for someone who just wants more logging.
// Checked here, ahead of ConnectorOptions.Parse's own (stricter) argument validation, since the
// loggers need to exist before Runner.Run does anything that might log.
var alwaysWriteFileLog = args.Contains("--file-log") || args.Contains("--debug");
var log = new CompositeLogger(new ConsoleLogger(), new FileLogger(alwaysWriteFileLog));

return (int)new Runner(log).Run(args, new TestAssemblyContextFactory());
