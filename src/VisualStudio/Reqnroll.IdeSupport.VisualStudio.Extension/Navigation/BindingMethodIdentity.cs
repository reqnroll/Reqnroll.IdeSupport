#nullable enable

using Newtonsoft.Json.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Navigation;

/// <summary>
/// The exact identity of a binding method as the server reports it (<c>methodIdentity</c>): the
/// declaring assembly's display name and the method's documentation-comment ID. Used to navigate to
/// a binding whose source is not on this machine.
/// </summary>
internal sealed record BindingMethodIdentity(string AssemblyIdentity, string DocumentationCommentId)
{
    /// <summary>
    /// Reads a <c>methodIdentity</c> object, or returns <see langword="null"/> when it is absent
    /// (an older server, or a binding discovery could not identify) or incomplete.
    /// </summary>
    public static BindingMethodIdentity? FromJson(JToken? token)
    {
        if (token is not JObject obj)
            return null;

        var assembly = obj["assemblyIdentity"]?.Value<string>();
        var id = obj["documentationCommentId"]?.Value<string>();
        return string.IsNullOrWhiteSpace(assembly) || string.IsNullOrWhiteSpace(id)
            ? null
            : new BindingMethodIdentity(assembly!, id!);
    }
}
