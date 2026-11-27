namespace Reqnroll.IdeSupport.LSP.Connector.Models;

/// <summary>
/// The exact identity of a binding method: the display name of the assembly that declares it and
/// its documentation-comment ID (e.g. <c>M:MyApp.Steps.Add(System.Int32,System.Int32)</c>).
/// </summary>
/// <remarks>
/// Both parts use formats Roslyn understands natively (<c>AssemblyIdentity.TryParseDisplayName</c>,
/// <c>DocumentationCommentId.GetSymbolsForDeclarationId</c>), so a consumer holding a compilation can
/// resolve the identity back to a symbol without any custom parsing. Unlike
/// <see cref="StepDefinition.Method"/>, which carries only the short declaring-type name, the
/// identity distinguishes same-named types in different namespaces and overloads.
/// </remarks>
/// <param name="AssemblyIdentity">The declaring assembly's display name, e.g. <c>MyApp.Specs, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null</c>.</param>
/// <param name="DocumentationCommentId">The method's documentation-comment ID; always starts with <c>M:</c>.</param>
public sealed record MethodIdentity(string AssemblyIdentity, string DocumentationCommentId);
