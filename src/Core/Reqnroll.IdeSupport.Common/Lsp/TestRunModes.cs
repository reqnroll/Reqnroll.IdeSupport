namespace Reqnroll.IdeSupport.Common.Lsp;

/// <summary>
/// The wire values of the optional <c>runMode</c> property on <c>reqnroll/testOutcomes/registerRun</c>
/// (issue #850). A client that registers because the user started a test run sends one of these so the
/// server can count the Run/Debug request as a passive feature; a registration without it (VS Code's
/// once-per-activation call) is not a user run and is not counted. The server ignores any other value.
/// </summary>
public static class TestRunModes
{
    /// <summary>A normal run.</summary>
    public const string Run = "Run";

    /// <summary>A run under the debugger.</summary>
    public const string Debug = "Debug";

    /// <summary>A run the client could not classify (Visual Studio's runsettings hook sees Run and Debug identically).</summary>
    public const string Unknown = "Unknown";
}
