#nullable disable

namespace Reqnroll.IdeSupport.VisualStudio.ProjectSystem;

/// <summary>DeveroomUserErrorCategory</summary>
public enum DeveroomUserErrorCategory
{
    /// <summary>An error raised during binding/step definition discovery.</summary>
    Discovery
}

/// <summary>DeveroomUserError</summary>
public class DeveroomUserError
{
    /// <summary>Gets or sets the message.</summary>
    public string Message { get; set; }
    //public SourceLocation SourceLocation { get; set; }
    /// <summary>Gets or sets the category.</summary>
    public DeveroomUserErrorCategory Category { get; set; }
}
