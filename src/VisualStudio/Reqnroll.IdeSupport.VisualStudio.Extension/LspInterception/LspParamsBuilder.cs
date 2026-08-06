#nullable enable

using System.Globalization;
using System.Text;
using Newtonsoft.Json;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Minimal hand-rolled JSON object builder for the outbound request params every LSP-calling
/// service in this project sends over <see cref="LspInterceptingPipe"/>. Replaces the near-identical
/// <c>BuildParams</c> string-concatenation method that had been duplicated across
/// <c>CommentToggleService</c>, <c>FindStepUsagesService</c>, <c>GoToHooksService</c>,
/// <c>GoToMatchingScenariosService</c>, <c>HookFeatureCodeLensService</c>,
/// <c>GherkinNavigationBarSymbolService</c>, and <c>StepCodeLensService</c> (issue #447).
/// </summary>
/// <remarks>
/// Deliberately stays a thin JSON-object writer rather than a typed-DTO/serializer layer: every
/// call site here builds a small, one-off params shape for a specific custom <c>reqnroll/*</c> or
/// standard LSP request, and a typed model per shape would be more ceremony than the two or three
/// fields involved actually warrant. What was worth centralizing is the string-escaping (every
/// prior copy called <see cref="JsonConvert.ToString(string)"/> the same way) and the manual
/// comma-joining between members, both of which are exactly the kind of easy-to-get-subtly-wrong
/// repetition duplication review flags.
/// </remarks>
internal sealed class LspParamsBuilder
{
    private readonly StringBuilder _members = new();
    private bool _hasMember;

    /// <summary>JSON-escapes and quotes <paramref name="value"/> using the same mechanism every prior <c>BuildParams</c> copy used.</summary>
    public static string EscapeString(string value) => JsonConvert.ToString(value);

    /// <summary>Builds <c>{"textDocument":{"uri":...}}</c> — the shape every file-scoped-only request (e.g. <c>textDocument/codeLens</c>) needs.</summary>
    public static string TextDocumentUri(string fileUri) =>
        new LspParamsBuilder().AddTextDocument(fileUri).Build();

    /// <summary>Builds <c>{"textDocument":{"uri":...},"position":{"line":...,"character":...}}</c> — the shape every position-scoped request needs, with no further members.</summary>
    public static string TextDocumentPosition(string fileUri, int line, int character) =>
        new LspParamsBuilder().AddTextDocument(fileUri).AddPosition(line, character).Build();

    /// <summary>Adds the standard <c>"textDocument":{"uri":...}</c> member.</summary>
    public LspParamsBuilder AddTextDocument(string fileUri) =>
        AddRaw("textDocument", $"{{\"uri\":{EscapeString(fileUri)}}}");

    /// <summary>Adds the standard <c>"position":{"line":...,"character":...}</c> member.</summary>
    public LspParamsBuilder AddPosition(int line, int character) =>
        AddRaw("position", $"{{\"line\":{line.ToString(CultureInfo.InvariantCulture)},\"character\":{character.ToString(CultureInfo.InvariantCulture)}}}");

    /// <summary>Adds a string-valued member, escaped and quoted.</summary>
    public LspParamsBuilder AddString(string name, string value) =>
        AddRaw(name, EscapeString(value));

    /// <summary>Adds a boolean-valued member.</summary>
    public LspParamsBuilder AddBool(string name, bool value) =>
        AddRaw(name, value ? "true" : "false");

    /// <summary>Adds an already-JSON-formatted member value verbatim — for object/array-shaped members this builder has no dedicated method for.</summary>
    public LspParamsBuilder AddRaw(string name, string rawJsonValue)
    {
        if (_hasMember)
            _members.Append(',');
        _members.Append(EscapeString(name)).Append(':').Append(rawJsonValue);
        _hasMember = true;
        return this;
    }

    /// <summary>Finishes the object, returning the complete params JSON string.</summary>
    public string Build() => "{" + _members + "}";
}
