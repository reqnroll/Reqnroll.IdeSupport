namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>
/// Returns the caller-supplied default for every key. Used when no .editorconfig applies.
/// </summary>
public sealed class NullEditorConfigOptions : IEditorConfigOptions
{
    /// <summary>The shared singleton instance.</summary>
    public static readonly NullEditorConfigOptions Instance = new();

    private NullEditorConfigOptions() { }

    /// <summary>Gets the option value for the specified key.</summary>
    public TResult GetOption<TResult>(string key, TResult defaultValue) => defaultValue;
    /// <summary>Gets the bool option value for the specified key.</summary>
    public bool GetBoolOption(string key, bool defaultValue) => defaultValue;
}
