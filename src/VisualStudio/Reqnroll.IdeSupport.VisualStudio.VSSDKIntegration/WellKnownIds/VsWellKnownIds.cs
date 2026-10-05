#nullable enable

using System;

namespace Reqnroll.IdeSupport.VisualStudio.WellKnownIds;

/// <summary>
/// Visual Studio identifiers this extension depends on that have <b>no managed SDK constant</b>.
/// </summary>
/// <remarks>
/// <para>
/// Prefer an SDK constant (<c>VSConstants</c>, <c>VsMenus</c>, <c>__VSPROPID</c>, …) whenever one
/// exists: the compiler then checks it. Issue #774 was a hand-typed property ID that silently named
/// the wrong property. Only values with no SDK equivalent belong here, each with where it came from.
/// </para>
/// <para>
/// How each value is kept honest:
/// <list type="bullet">
///   <item>Values defined in a VS SDK header are asserted against that header by
///   <c>VsSdkHeaderConstantsTests</c>, which reads the headers shipped in the
///   <c>Microsoft.VSSDK.BuildTools</c> NuGet package (no VS install needed).</item>
///   <item>Undocumented values copied out of VS assemblies can only be checked against a running VS:
///   <c>VsWellKnownIdsSelfCheck</c> resolves their canonical command names at package load and logs
///   the result, flagging any mismatch.</item>
///   <item><c>MagicValueSourceGuardTests</c> fails the build when a new GUID or content-type literal
///   appears outside this file and the small allow-list of files that own their own identifiers.</item>
/// </list>
/// </para>
/// </remarks>
public static class VsWellKnownIds
{
    /// <summary>
    /// Content type (and VS.Extensibility document type) name of <c>.feature</c> files. Registered by
    /// this extension itself in <c>GherkinDocumentType</c>; every <c>[ContentType]</c> export and
    /// <c>ActivationConstraint.EditorContentType</c> for <c>.feature</c> files must use this constant.
    /// </summary>
    public const string GherkinContentType = "Gherkin";

    /// <summary>
    /// <c>IDG_VS_CODEWIN_NAVIGATETOLOCATION</c> (<c>vsshlids.h</c>): the group in the code-editor
    /// context menu that hosts "Go To Definition" / "Find All References". Its parent command set is
    /// <c>VsMenus.guidSHLMainMenu</c>. Header-checked.
    /// </summary>
    public const int IDG_VS_CODEWIN_NAVIGATETOLOCATION = 0x02B1;

    /// <summary>
    /// The core editor's own command set. <c>Edit.ToggleLineComment</c> is not in VSStd2K and has no
    /// <c>VSConstants</c> entry. Found in the CommandBindings of VS's
    /// <c>Microsoft.VisualStudio.Editor.Implementation.dll</c>, which maps
    /// <c>{160961B3-909D-4B28-9353-A1BEF587B4A6}:48</c> to <c>ToggleLineCommentCommandArgs</c>
    /// (issue #747). Undocumented: runtime-checked by <c>VsWellKnownIdsSelfCheck</c>.
    /// </summary>
    public static readonly Guid EditorCommandSet = new("160961B3-909D-4B28-9353-A1BEF587B4A6");

    /// <summary><c>Edit.ToggleLineComment</c> within <see cref="EditorCommandSet"/>. See there.</summary>
    public const uint CmdIdToggleLineComment = 48;

    /// <summary>Canonical VS command name of <see cref="CmdIdToggleLineComment"/>.</summary>
    public const string ToggleLineCommentCommandName = "Edit.ToggleLineComment";

    /// <summary>
    /// MEF <c>[Name(...)]</c> of VS's own <c>INavigableSymbolSourceProvider</c> for Ctrl+Click/Ctrl+hover
    /// (<c>NavigableSymbolSourceProvider</c> in <c>Microsoft.VisualStudio.LanguageServer.Client.Implementation.dll</c>,
    /// confirmed by decompiling that assembly, issue #761). No SDK constant exists for it. Used as the
    /// <c>[Order(Before = ...)]</c> target of our own provider so ours runs first for Gherkin views.
    /// </summary>
    public const string LspNavigableSymbolSourceProviderName = "LSP NavigableSymbolSourceProvider";
}
