using Reqnroll.IdeSupport.Common.Configuration.EditorConfig;
using Reqnroll.IdeSupport.Common.Configuration.Model;
using Xunit;

namespace Reqnroll.IdeSupport.Common.Tests.Configuration;

public class CSharpCodeGenerationConfigurationTests
{
    [Fact]
    public void UseFileScopedNamespaces_WhenFileScopedSet_ReturnsTrue()
    {
        var config = new CSharpCodeGenerationConfiguration
        {
            NamespaceDeclarationStyle = "file_scoped:warning"
        };
        Assert.True(config.UseFileScopedNamespaces);
    }

    [Fact]
    public void UseFileScopedNamespaces_WhenBlockScopedSet_ReturnsFalse()
    {
        var config = new CSharpCodeGenerationConfiguration
        {
            NamespaceDeclarationStyle = "block_scoped"
        };
        Assert.False(config.UseFileScopedNamespaces);
    }

    [Fact]
    public void UseFileScopedNamespaces_WhenDefaultValue_ReturnsFalse()
    {
        var config = new CSharpCodeGenerationConfiguration();
        Assert.False(config.UseFileScopedNamespaces);
    }

    [Fact]
    public void UseFileScopedNamespaces_WhenNullValue_ReturnsFalse()
    {
        var config = new CSharpCodeGenerationConfiguration
        {
            NamespaceDeclarationStyle = null
        };
        Assert.False(config.UseFileScopedNamespaces);
    }

    [Fact]
    public void UseFileScopedNamespaces_WhenUnknownValue_ReturnsFalse()
    {
        var config = new CSharpCodeGenerationConfiguration
        {
            NamespaceDeclarationStyle = "unknown_style"
        };
        Assert.False(config.UseFileScopedNamespaces);
    }

    // ── UpdateFromEditorConfig ────────────────────────────────────────────────

    [Fact]
    public void UpdateFromEditorConfig_WhenFileScopedValue_SetsCorrectValue()
    {
        var config = new CSharpCodeGenerationConfiguration();
        var options = new TestEditorConfigOptions("file_scoped:silent");

        options.UpdateFromEditorConfig(config);

        Assert.Equal("file_scoped:silent", config.NamespaceDeclarationStyle);
        Assert.True(config.UseFileScopedNamespaces);
    }

    [Fact]
    public void UpdateFromEditorConfig_WhenBlockScopedValue_SetsCorrectValue()
    {
        var config = new CSharpCodeGenerationConfiguration();
        var options = new TestEditorConfigOptions("block_scoped");

        options.UpdateFromEditorConfig(config);

        Assert.Equal("block_scoped", config.NamespaceDeclarationStyle);
        Assert.False(config.UseFileScopedNamespaces);
    }

    [Fact]
    public void UpdateFromEditorConfig_WhenNoValue_KeepsDefault()
    {
        var config = new CSharpCodeGenerationConfiguration();
        var options = new TestEditorConfigOptions(null);

        options.UpdateFromEditorConfig(config);

        Assert.Equal("block_scoped", config.NamespaceDeclarationStyle);
        Assert.False(config.UseFileScopedNamespaces);
    }
}

file sealed class TestEditorConfigOptions : IEditorConfigOptions
{
    private readonly string? _namespaceStyle;

    public TestEditorConfigOptions(string? namespaceStyle) => _namespaceStyle = namespaceStyle;

    public TResult GetOption<TResult>(string editorConfigKey, TResult defaultValue)
    {
        if (editorConfigKey == "csharp_style_namespace_declarations" && _namespaceStyle != null)
            return (TResult)(object)_namespaceStyle;
        return defaultValue;
    }

    public bool GetBoolOption(string editorConfigKey, bool defaultValue) => defaultValue;
}
