namespace Reqnroll.IdeSupport.LSP.Server.Logging;

/// <summary>
/// Maps the <c>--ide</c> identifier onto the IDE segment of the server's log file names
/// (<c>reqnroll-{prefix}-server-*.log</c>, <c>-protocol-</c>, <c>-crash-</c>). Single source of truth so
/// the application, protocol and crash loggers can never disagree about it (issue #791).
/// </summary>
internal static class IdeLogPrefix
{
    /// <summary>Prefix used when <c>--ide</c> is absent or unrecognized; avoids misattributing to a known IDE.</summary>
    internal const string Unknown = "lsp";

    internal static string From(string? ide) => ide switch
    {
        "visualstudio" => "vs",
        "vscode"       => "vscode",
        "rider"        => "rider",
        _              => Unknown,
    };
}
