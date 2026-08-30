#nullable disable

using System.Collections.Generic;

namespace Reqnroll.IdeSupport.Common.ProjectSystem;

/// <summary>IIdeSupportErrorListServices</summary>
public interface IIdeSupportErrorListServices
{
    /// <summary>Clears all previously reported errors in the given category.</summary>
    void ClearErrors(IdeSupportUserErrorCategory category);
    /// <summary>Adds the given errors to the error list.</summary>
    void AddErrors(IEnumerable<IdeSupportUserError> errors);
}
