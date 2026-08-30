using System;
using System.Linq;

namespace Reqnroll.IdeSupport.Common.ProjectSystem;

/// <summary>IDeveroomWindowManager</summary>
public interface IDeveroomWindowManager
{
    /// <summary>Shows a modal dialog for the specified view-model.</summary>
    bool? ShowDialog<TViewModel>(TViewModel viewModel);
}
