namespace Reqnroll.IdeSupport.Common.ProjectSystem;

/// <summary>IIdeSupportOutputPaneServices</summary>
public interface IIdeSupportOutputPaneServices
{
    /// <summary>Writes a line of text to the output pane.</summary>
    void WriteLine(string text);
    /// <summary>Queues a line of text to be written to the output pane asynchronously.</summary>
    void SendWriteLine(string text);
    /// <summary>Brings the output pane to the foreground.</summary>
    void Activate();
}
