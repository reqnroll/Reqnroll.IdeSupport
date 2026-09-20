using Newtonsoft.Json;

namespace Reqnroll.IdeSupport.LSP.Server.Protocol;

/// <summary>
/// Payload shape for the <c>reqnrollTestOutcomesProvider</c> entry Program.cs writes into
/// <c>ServerCapabilities.ExtensionData</c> during <c>initialize</c> -- a real, named C# type for
/// the capability's data instead of an anonymous object, even though OmniSharp's <c>InitializeResult
/// .Capabilities</c> being init-only rules out a proper <c>ServerCapabilities</c> subclass with this
/// as a first-class sibling property (the wire shape Roslyn's own LSP server uses for its own custom
/// capabilities, via <c>VSInternalServerCapabilities</c>). See ApplyTestOutcomesCapability's remarks
/// for why <c>ExtensionData</c> is the fallback and why it's still wire-safe.
///
/// Only the feature's existence and method names are advertised here -- the actual per-run
/// endpoint+token is deliberately NOT part of this one-time handshake payload (see
/// RegisterTestRunHandler/TestOutcomeTcpListener's remarks): a capability describes static feature
/// availability, while a run's credential must stay short-lived and single-use, minted fresh per
/// request instead.
/// </summary>
public sealed class ReqnrollTestOutcomesOptions
{
    [JsonProperty("registerRunMethod")]
    public required string RegisterRunMethod { get; init; }

    [JsonProperty("getOutcomeMethod")]
    public required string GetOutcomeMethod { get; init; }

    [JsonProperty("changedNotification")]
    public required string ChangedNotification { get; init; }
}
