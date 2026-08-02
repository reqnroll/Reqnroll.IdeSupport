namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// Resource-id constants for <c>CodeLensResources.resx</c> (embedded alongside this type so
/// <see cref="Microsoft.VisualStudio.Utilities.LocalizedNameAttribute"/> can resolve it purely from
/// <c>typeof(CodeLensResources)</c> — its namespace+name must match the resx's manifest resource
/// name, which the SDK derives from <c>RootNamespace</c> + folder path + file name).
/// </summary>
internal static class CodeLensResources
{
    public const string HookMatchCountProviderName = nameof(HookMatchCountProviderName);
    public const string StepHooksProviderName = nameof(StepHooksProviderName);
}
