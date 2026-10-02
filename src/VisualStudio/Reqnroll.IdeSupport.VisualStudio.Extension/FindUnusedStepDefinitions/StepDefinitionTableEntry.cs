using System.Collections.Generic;
using Microsoft.VisualStudio.Shell.TableManager;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;

/// <summary>
/// A single row in the Find All References table window, representing one unused
/// step-definition binding.
/// </summary>
internal sealed class StepDefinitionTableEntry : ITableEntry
{
    private readonly Dictionary<string, object> _values = new Dictionary<string, object>();

    /// <summary>Unique identity used by the table manager for row deduplication.</summary>
    public object Identity => _values;

    /// <summary>Always <c>true</c> — every column key is settable on this entry.</summary>
    public bool CanSetValue(string keyName) => true;

    /// <summary>Reads a previously-set column value by key.</summary>
    public bool TryGetValue(string keyName, out object content) =>
        _values.TryGetValue(keyName, out content!);

    /// <summary>Sets a column value by key.</summary>
    public bool TrySetValue(string keyName, object content)
    {
        _values[keyName] = content;
        return true;
    }
}
