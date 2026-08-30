#nullable disable

using System.Collections.Generic;

namespace Reqnroll.IdeSupport.VisualStudio.ProjectSystem;

/// <summary>IIdeSupportErrorListServices</summary>
public interface IIdeSupportErrorListServices
{
    /// <summary>Clears all previously reported errors in the given category.</summary>
    void ClearErrors(IdeSupportUserErrorCategory category);
    /// <summary>Adds the given errors to the error list.</summary>
    void AddErrors(IEnumerable<IdeSupportUserError> errors);
}

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
