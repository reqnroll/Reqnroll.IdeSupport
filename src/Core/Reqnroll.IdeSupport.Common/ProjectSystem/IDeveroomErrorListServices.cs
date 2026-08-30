#nullable disable

using System.Collections.Generic;

namespace Reqnroll.IdeSupport.VisualStudio.ProjectSystem;

/// <summary>IDeveroomErrorListServices</summary>
public interface IDeveroomErrorListServices
{
    /// <summary>Clears all previously reported errors in the given category.</summary>
    void ClearErrors(DeveroomUserErrorCategory category);
    /// <summary>Adds the given errors to the error list.</summary>
    void AddErrors(IEnumerable<DeveroomUserError> errors);
}
