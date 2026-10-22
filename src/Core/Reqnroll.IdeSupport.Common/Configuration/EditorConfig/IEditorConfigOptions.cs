namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>
/// Read-only view of EditorConfig settings that apply to a specific file path.
/// </summary>
public interface IEditorConfigOptions
{
    /// <summary>Returns the value for <paramref name="key"/>, or <paramref name="defaultValue"/> if the key is absent.</summary>
    TResult GetOption<TResult>(string key, TResult defaultValue);

    /// <summary>Convenience overload for boolean keys.</summary>
    bool GetBoolOption(string key, bool defaultValue);
}
