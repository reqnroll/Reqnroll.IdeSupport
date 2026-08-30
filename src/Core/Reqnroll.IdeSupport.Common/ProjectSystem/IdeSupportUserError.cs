#nullable disable

namespace Reqnroll.IdeSupport.Common.ProjectSystem;

/// <summary>IdeSupportUserErrorCategory</summary>
public enum IdeSupportUserErrorCategory
{
    /// <summary>An error raised during binding/step definition discovery.</summary>
    Discovery
}

/// <summary>IdeSupportUserError</summary>
public class IdeSupportUserError
{
    /// <summary>Gets or sets the message.</summary>
    public string Message { get; set; }
    //public SourceLocation SourceLocation { get; set; }
    /// <summary>Gets or sets the category.</summary>
    public IdeSupportUserErrorCategory Category { get; set; }
}
