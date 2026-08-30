#nullable disable

using System.Collections.Generic;

namespace Reqnroll.IdeSupport.Common.ProjectSystem;

/// <summary>IDeveroomErrorListServices</summary>
public interface IDeveroomErrorListServices
{
    /// <summary>Clears all previously reported errors in the given category.</summary>
    void ClearErrors(DeveroomUserErrorCategory category);
    /// <summary>Adds the given errors to the error list.</summary>
    void AddErrors(IEnumerable<DeveroomUserError> errors);
}

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
