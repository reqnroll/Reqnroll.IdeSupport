using Newtonsoft.Json;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;

/// <summary>
/// Visual Studio's priority levels for a code action, as its LSP client reads them from
/// <c>_vs_priority</c>. A higher level sorts first. An action with no level sorts after
/// <see cref="Normal"/> and <see cref="High"/> and before <see cref="Low"/> and <see cref="Lowest"/>.
/// </summary>
public enum VsCodeActionPriority
{
    /// <summary>Sorts after every other level and after actions with no level.</summary>
    Lowest = 0,

    /// <summary>Sorts after actions with no level.</summary>
    Low = 1,

    /// <summary>Sorts before actions with no level.</summary>
    Normal = 2,

    /// <summary>Sorts before every other level.</summary>
    High = 3,
}

/// <summary>
/// A <see cref="CodeAction"/> that also carries Visual Studio's <c>_vs_priority</c> field. Visual
/// Studio sorts the lightbulb by that field and then alphabetically by title, ignoring the order of
/// the response. Other clients ignore the field.
/// </summary>
public sealed record VsPrioritizedCodeAction : CodeAction
{
    /// <summary>Copies <paramref name="action"/> and gives it <paramref name="priority"/>.</summary>
    public VsPrioritizedCodeAction(CodeAction action, VsCodeActionPriority priority)
        : base(action)
    {
        Priority = (int)priority;
    }

    /// <summary>
    /// The <see cref="VsCodeActionPriority"/> Visual Studio sorts this action by. Kept as a number
    /// so it goes on the wire as one, whatever enum converters the serializer has.
    /// </summary>
    [JsonProperty("_vs_priority")]
    public int Priority { get; init; }
}
