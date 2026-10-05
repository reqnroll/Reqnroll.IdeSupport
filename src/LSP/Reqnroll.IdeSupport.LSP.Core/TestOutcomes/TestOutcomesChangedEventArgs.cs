#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.TestOutcomes;

namespace Reqnroll.IdeSupport.LSP.Core.TestOutcomes;

/// <summary>Raised after the store changes; <see cref="Keys"/> lists the affected methods.</summary>
public sealed class TestOutcomesChangedEventArgs : EventArgs
{
    public TestOutcomesChangedEventArgs(IReadOnlyCollection<TestOutcomeKey> keys, int revision)
    {
        Keys = keys;
        Revision = revision;
    }

    public IReadOnlyCollection<TestOutcomeKey> Keys { get; }
    public int Revision { get; }
}
