using ReqnrollConnector;
using ReqnrollConnector.AssemblyLoading;
using ReqnrollConnector.Logging;

// FileLogger (issue #628) gives a Connector crash a durable artifact of its own, independent of
// whatever the LSP server managed to capture from this process's stdout/stderr — but only writes
// unconditionally when --debug (the existing "give me more diagnostics" flag) is present; otherwise
// it buffers and only persists a file if something actually goes wrong (issue #637). Checked here,
// ahead of ConnectorOptions.Parse's own (stricter) argument validation, since the loggers need to
// exist before Runner.Run does anything that might log.
var alwaysWriteFileLog = args.Contains("--debug");
var log = new CompositeLogger(new ConsoleLogger(), new FileLogger(alwaysWriteFileLog));

return (int)new Runner(log).Run(args, new TestAssemblyContextFactory());
