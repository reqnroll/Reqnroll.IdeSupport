using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading;
using JetBrains.Application.Parts;
using JetBrains.Application.Threading;
using JetBrains.DocumentModel;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.UnitTestFramework.Persistence;

namespace Reqnroll.IdeSupport.Rider.Backend.UnitTesting;

/// <summary>
/// Re-runs <see cref="FeatureTestGutterStage"/> on the <c>.feature</c> documents it has analysed when Rider's set
/// of discovered tests changes. Without it, a <c>.feature</c> file opened before discovery of its code-behind has
/// finished keeps no marks until it is edited, because nothing else re-analyses it when tests appear in another
/// file. Changes are debounced, since discovery adds tests in many small batches.
/// </summary>
[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public class FeatureTestGutterRefresher
{
    private const int DebounceMilliseconds = 1000;

    private readonly Lifetime _lifetime;
    private readonly ISolution _solution;
    private readonly IShellLocks _locks;
    private readonly Timer _timer;
    private readonly object _gate = new object();
    private readonly List<WeakReference<IDocument>> _documents = new List<WeakReference<IDocument>>();

    // IDaemon is created on the primary thread, so this any-thread component looks it up when it needs it (on the
    // primary thread, by then long created) rather than taking it as a dependency.
    public FeatureTestGutterRefresher(Lifetime lifetime, ISolution solution, IUnitTestElementStore elementStore, IShellLocks locks)
    {
        _lifetime = lifetime;
        _solution = solution;
        _locks = locks;
        _timer = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);

        NotifyCollectionChangedEventHandler onChanged = (_, _) => OnTestsChanged();
        elementStore.CollectionChanged += onChanged;
        lifetime.OnTermination(() =>
        {
            elementStore.CollectionChanged -= onChanged;
            _timer.Dispose();
        });
    }

    /// <summary>Remembers a <c>.feature</c> document to re-analyse when tests change.</summary>
    public void Track(IDocument document)
    {
        lock (_gate)
        {
            _documents.RemoveAll(reference => !reference.TryGetTarget(out _));
            foreach (var reference in _documents)
            {
                if (reference.TryGetTarget(out var tracked) && ReferenceEquals(tracked, document))
                    return;
            }

            _documents.Add(new WeakReference<IDocument>(document));
        }
    }

    private void OnTestsChanged()
    {
        lock (_gate)
        {
            if (_documents.Count == 0 || !_lifetime.IsAlive)
                return;
        }

        try
        {
            _timer.Change(DebounceMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // The solution closed while discovery was still reporting.
        }
    }

    private void Refresh()
    {
        if (!_lifetime.IsAlive)
            return;

        _locks.ExecuteOrQueueReadLockEx(_lifetime, "Reqnroll: refresh .feature test gutter marks", () =>
        {
            List<IDocument> documents;
            lock (_gate)
            {
                documents = new List<IDocument>(_documents.Count);
                foreach (var reference in _documents)
                {
                    if (reference.TryGetTarget(out var document))
                        documents.Add(document);
                }
            }

            if (documents.Count == 0)
                return;

            var daemon = _solution.GetComponent<IDaemon>();
            foreach (var document in documents)
                daemon.Invalidate("Reqnroll: discovered tests changed", document);
        });
    }
}
