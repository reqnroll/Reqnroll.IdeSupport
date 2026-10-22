using System.Reflection;

namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>EditorConfigOptionsExtensions</summary>
public static class EditorConfigOptionsExtensions
{
    /// <summary>
    /// For each property on <paramref name="config"/> annotated with
    /// <see cref="EditorConfigSettingAttribute"/>, reads the corresponding key from
    /// <paramref name="options"/> and replaces the property value when the key is present.
    /// Supported property types: <see langword="bool"/>, <see langword="int"/>,
    /// <see langword="string"/>.
    /// </summary>
    public static void UpdateFromEditorConfig<T>(this IEditorConfigOptions options, T config)
        where T : class
    {
        foreach (var prop in typeof(T).GetProperties())
        {
            var attr = prop.GetCustomAttribute<EditorConfigSettingAttribute>();
            if (attr is null) continue;

            var key = attr.EditorConfigSettingName;
            if (prop.PropertyType == typeof(bool))
                prop.SetValue(config, options.GetBoolOption(key, (bool)prop.GetValue(config)!));
            else if (prop.PropertyType == typeof(int))
                prop.SetValue(config, options.GetOption(key, (int)prop.GetValue(config)!));
            else if (prop.PropertyType == typeof(string))
                prop.SetValue(config, options.GetOption<string?>(key, (string?)prop.GetValue(config)));
        }
    }
}
