using System;
using System.Linq;

namespace Reqnroll.IdeSupport.Common.ProjectSystem;

/// <summary>IIdeSupportWindowManager</summary>
public interface IIdeSupportWindowManager
{
    /// <summary>Shows a modal dialog for the specified view-model.</summary>
    bool? ShowDialog<TViewModel>(TViewModel viewModel);
}
