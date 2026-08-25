#nullable enable

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Core.Matching;

/// <summary>
/// A stable identity for a discovered step-definition binding, used to key
/// <see cref="IBindingMatchService"/>'s reverse index (issue #471 — borrows clangd's
/// <c>SymbolID</c> shape: identity is hashed from stable content, not from source position).
/// </summary>
/// <remarks>
/// Computed from <c>(StepDefinitionType, Implementation.Method, Implementation.ParameterTypes,
/// Expression)</c> — everything needed is already present on a <see cref="ProjectStepDefinitionBinding"/>,
/// so no project-owner or attribute-ordinal plumbing is required through the parser/connector
/// importer. <see cref="Expression"/> disambiguates the rare case of two attributes of the same
/// block on the same method with different expression text (see
/// <c>ProjectBindingRegistry.HasExpressionChanges</c> for the same edge case, handled there as a
/// multiset). Stable across repeated re-parses of unchanged source: <c>Method</c>,
/// <c>ParameterTypes</c>, and attribute/block enumeration order are pure functions of the syntax
/// tree's content.
/// </remarks>
public readonly record struct BindingId(ulong Value)
{
    // ASCII Unit Separator (0x1F): not valid in any identity component we hash (method names,
    // expressions, etc. are never going to contain a raw control character), so it can't be
    // smuggled in to make two distinct component sequences collide, e.g. ("ab","c") vs ("a","bc").
    private const char Separator = '\u001F';

    /// <summary>Computes the <see cref="BindingId"/> for a discovered step-definition binding.</summary>
    public static BindingId For(ProjectStepDefinitionBinding binding)
    {
        if (binding == null) throw new ArgumentNullException(nameof(binding));
        return Compute(binding.StepDefinitionType, binding.Implementation.Method,
            binding.Implementation.ParameterTypes, binding.Expression);
    }

    /// <summary>Computes the <see cref="BindingId"/> from its individual identity components.</summary>
    public static BindingId Compute(ScenarioBlock stepType, string method,
        IReadOnlyList<string> parameterTypes, string? expression)
    {
        var canonical = new StringBuilder();
        canonical.Append((int)stepType).Append(Separator);
        canonical.Append(method).Append(Separator);
        foreach (var parameterType in parameterTypes)
            canonical.Append(parameterType).Append(Separator);
        canonical.Append(expression ?? string.Empty);

        // SHA-256 over the canonical identity string, truncated to its first 8 bytes -- an
        // off-the-shelf BCL primitive rather than a hand-rolled mixing function; the truncation
        // is fine here since this key only needs collision-resistance for an in-process lookup,
        // not cryptographic guarantees. Uses the instance Create()/ComputeHash API (rather than
        // the static SHA256.HashData helper) since this project targets netstandard2.0.
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
        return new BindingId(BinaryPrimitives.ReadUInt64BigEndian(hash));
    }

    /// <summary>Returns the id as a compact hex string, suitable for round-tripping through JSON (e.g. <c>CodeLens.Data</c>).</summary>
    public override string ToString() => Value.ToString("x16");

    /// <summary>Parses a <see cref="BindingId"/> from the hex string produced by <see cref="ToString"/>.</summary>
    public static bool TryParse(string? text, out BindingId id)
    {
        if (ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            id = new BindingId(value);
            return true;
        }
        id = default;
        return false;
    }
}
