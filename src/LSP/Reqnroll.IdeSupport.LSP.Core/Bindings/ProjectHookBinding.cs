#nullable disable

using Gherkin.Ast;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.Bindings;

/// <summary>A discovered Reqnroll hook binding: which lifecycle event it runs on, its ordering, and its scope.</summary>
public class ProjectHookBinding : ProjectBinding
{
    /// <summary>The hook order used when none was specified.</summary>
    public const int DefaultHookOrder = 10000;

    /// <summary>The lifecycle event this hook runs on.</summary>
    public HookType HookType { get; }
    /// <summary>The relative execution order among hooks of the same <see cref="HookType"/> (lower runs first).</summary>
    public int HookOrder { get; }

    /// <summary>Creates a hook binding, defaulting its order to <see cref="DefaultHookOrder"/> when not specified.</summary>
    public ProjectHookBinding(ProjectBindingImplementation implementation, BindingScope scope, HookType hookType,
        int? hookOrder, string error, SourceLocation errorLocation = null)
        : base(implementation, scope, error, errorLocation)
    {
        HookType = hookType;
        HookOrder = hookOrder ?? DefaultHookOrder;
    }

    /// <summary>True when this hook's scope allows it to run for the given scenario.</summary>
    public bool Match(Scenario scenario, IGherkinDocumentContext context)
    {
        if (!MatchScope(context))
            return false;

        return true;
    }

    /// <summary>Returns a short description including the hook type, scope (if any), and implementation.</summary>
    public override string ToString() =>
        Scope == null ? $"[{HookType}]: {Implementation}" : $"[{HookType}({Scope})]: {Implementation}";
}