#nullable enable

using System;
using System.Threading;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Activation;

/// <summary>What <see cref="ScratchFileActivationTrigger"/> decides to do.</summary>
internal enum ActivationTriggerDecision
{
    /// <summary>Open the scratch file: a <c>.feature</c> document is open and the provider was never activated.</summary>
    Trigger,

    /// <summary>Do nothing: the user turned the trigger off.</summary>
    SkipDisabled,

    /// <summary>Do nothing: VS already activated the provider.</summary>
    SkipAlreadyActivated,

    /// <summary>Do nothing: no <c>.feature</c> document is open, so nothing is missing any features.</summary>
    SkipNoFeatureDocument,
}
