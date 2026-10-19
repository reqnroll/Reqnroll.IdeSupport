#nullable enable

using Newtonsoft.Json;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Features.DocumentActivated;

/// <summary>Parameters for the <c>reqnroll/documentActivated</c> notification.</summary>
public sealed class DocumentActivatedParams
{
    /// <summary>Gets or sets the uri.</summary>
    [JsonProperty("uri")]
    public DocumentUri Uri { get; set; } = null!;
}
