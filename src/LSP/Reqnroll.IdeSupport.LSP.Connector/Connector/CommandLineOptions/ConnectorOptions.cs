namespace ReqnrollConnector.CommandLineOptions;

public record ConnectorOptions(bool DebugMode)
{
    public const string DiscoveryCommandName = "discovery";

    public static ConnectorOptions Parse(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("Command is missing!");

        var commandArgsList = args.Skip(1).ToList();
        int debugArgIndex = commandArgsList.IndexOf("--debug");
        var debugMode = false;
        if (debugArgIndex >= 0)
        {
            debugMode = true;
            commandArgsList.RemoveAt(debugArgIndex);
        }

        // --file-log (issue #637) is only read by Program.cs, directly off the raw args, before
        // this method ever runs — the logger has to exist before parsing does, so it can log a
        // parse failure. It's stripped here purely so it doesn't get misread as a positional
        // argument (assembly/config path) below, the same reason --debug is stripped above.
        int fileLogArgIndex = commandArgsList.IndexOf("--file-log");
        if (fileLogArgIndex >= 0)
            commandArgsList.RemoveAt(fileLogArgIndex);

        var commandName = args[0];
        var commandArgs = commandArgsList.ToArray();

        return commandName switch
        {
            DiscoveryCommandName => DiscoveryOptions.Parse(commandArgs, debugMode),
            _ => throw new ArgumentException($"Invalid command: {commandName}")
        };
    }
}
