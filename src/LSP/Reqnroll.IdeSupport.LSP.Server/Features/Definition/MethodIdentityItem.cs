#nullable enable

using Newtonsoft.Json;
using Reqnroll.IdeSupport.LSP.Connector.Models;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Definition;

/// <summary>
/// The exact identity of a binding method on the wire to clients: the declaring assembly's display
/// name and the method's documentation-comment ID. A client that can resolve symbols (Visual Studio
/// through Roslyn) uses it to navigate to a binding whose source is not on this machine, letting the
/// IDE choose Source Link, embedded source or decompilation.
/// </summary>
public sealed class MethodIdentityItem
{
    /// <summary>The declaring assembly's display name, e.g. <c>MyApp.Specs, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null</c>.</summary>
    [JsonProperty("assemblyIdentity")]
    public string AssemblyIdentity { get; set; } = "";

    /// <summary>The method's documentation-comment ID, e.g. <c>M:MyApp.Steps.Add(System.Int32)</c>.</summary>
    [JsonProperty("documentationCommentId")]
    public string DocumentationCommentId { get; set; } = "";

    /// <summary>The wire form of <paramref name="identity"/>, or <see langword="null"/> when there is none.</summary>
    internal static MethodIdentityItem? From(MethodIdentity? identity) =>
        identity is null
            ? null
            : new MethodIdentityItem
            {
                AssemblyIdentity       = identity.AssemblyIdentity,
                DocumentationCommentId = identity.DocumentationCommentId,
            };
}
