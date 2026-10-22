using System;

namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>
/// Specifies the <c>.editorconfig</c> setting name that maps to this property.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class EditorConfigSettingAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="EditorConfigSettingAttribute"/> class.</summary>
    public EditorConfigSettingAttribute(string editorConfigSettingName)
    {
        EditorConfigSettingName = editorConfigSettingName;
    }

    /// <summary>Gets the name of the <c>.editorconfig</c> setting.</summary>
    public string EditorConfigSettingName { get; }
}