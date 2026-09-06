using ReqnrollConnector;
using ReqnrollConnector.AssemblyLoading;
using ReqnrollConnector.Logging;

// FileLogger (issue #628) gives a Connector crash a durable artifact of its own, independent of
// whatever the LSP server managed to capture from this process's stdout/stderr.
var log = new CompositeLogger(new ConsoleLogger(), new FileLogger());

return (int)new Runner(log).Run(args, new TestAssemblyContextFactory());
