# EditorConfig Support Design

> **Status:** Draft for review  
> **Scope:** `Reqnroll.IdeSupport.Common` + `Reqnroll.IdeSupport.LSP.Server`

---

## 1. Context and Current State

### What exists today

Several configuration classes in `Reqnroll.IdeSupport.Common` carry `[EditorConfigSetting]` attributes on their properties, marking each property with its corresponding `.editorconfig` key:

| Class | Properties marked |
|-------|-------------------|
| [`GherkinFormatConfiguration`](../src/Core/Reqnroll.IdeSupport.Common/Configuration/GherkinFormatConfiguration.cs) | 10 `gherkin_*` formatting properties |
| [`CSharpCodeGenerationConfiguration`](../src/Core/Reqnroll.IdeSupport.Common/Configuration/CSharpCodeGenerationConfiguration.cs) | `csharp_style_namespace_declarations` |

The [`EditorConfigSettingAttribute`](../src/Core/Reqnroll.IdeSupport.Common/Configuration/EditorConfigSettingAttribute.cs) is defined but **nothing currently reads `.editorconfig` files** — the attribute is intent, not implementation. Tests that exercise the `UpdateFromEditorConfig` contract and the `IEditorConfigOptions` interface are present but wrapped in `#if false` pending this work (see [CSharpCodeGenerationConfigurationTests.cs](../tests/Core/Reqnroll.IdeSupport.Common.Tests/Configuration/CSharpCodeGenerationConfigurationTests.cs) and [StubEditorConfigOptionsProvider.cs](../tests/VisualStudio/Reqnroll.VisualStudio.VsxStubs/StubEditorConfigOptionsProvider.cs)).

Configuration today is loaded exclusively from JSON/XML project files (`reqnroll.json`, `specflow.json`, `App.config`, `deveroom.json`) by [`ProjectScopeDeveroomConfigurationProvider`](../src/Core/Reqnroll.IdeSupport.Common/ProjectSystem/Configuration/ProjectScopeDeveroomConfigurationProvider.cs).

### What the legacy VS Extension had

The original `Reqnroll.VisualStudio` extension had `IEditorConfigOptions` and `IEditorConfigOptionsProvider` interfaces that wrapped VS's own EditorConfig APIs. These were not ported when `Reqnroll.IdeSupport.Common` was extracted. The VS-hosted implementation obtained EditorConfig values via `IEditorOptions` and `IEditorConfigOptionsProvider` (VSSDK services), so it had no file-system reader of its own.

### Goal

The LSP server must read `.editorconfig` files directly from disk. The VS Extension, VS Code extension, and Rider plugin all communicate with the same server process; none of them forwards EditorConfig data over the LSP connection. The server is therefore responsible for its own EditorConfig lookup.

At minimum the LSP server must provide the same capability that was present in the VS Extension: gherkin formatting settings (`gherkin_*`) and the C# namespace declaration style (`csharp_style_namespace_declarations`) read from `.editorconfig`.

---

## 2. Settings Covered

### Reqnroll-specific keys

These keys are defined by Reqnroll and have no standard EditorConfig meaning. They are only read by this implementation.

| `.editorconfig` key | Config property | Type | Default |
|---------------------|-----------------|------|---------|
| `gherkin_indent_feature_children` | `GherkinFormatConfiguration.IndentFeatureChildren` | bool | `false` |
| `gherkin_indent_rule_children` | `GherkinFormatConfiguration.IndentRuleChildren` | bool | `false` |
| `gherkin_indent_steps` | `GherkinFormatConfiguration.IndentSteps` | bool | `true` |
| `gherkin_indent_and_steps` | `GherkinFormatConfiguration.IndentAndSteps` | bool | `false` |
| `gherkin_normalize_repeated_keywords` | `GherkinFormatConfiguration.NormalizeRepeatedKeywords` | bool | `true` |
| `gherkin_indent_datatable` | `GherkinFormatConfiguration.IndentDataTable` | bool | `true` |
| `gherkin_indent_docstring` | `GherkinFormatConfiguration.IndentDocString` | bool | `true` |
| `gherkin_indent_examples` | `GherkinFormatConfiguration.IndentExamples` | bool | `false` |
| `gherkin_indent_examples_table` | `GherkinFormatConfiguration.IndentExamplesTable` | bool | `true` |
| `gherkin_table_cell_padding_size` | `GherkinFormatConfiguration.TableCellPaddingSize` | int | `1` |
| `gherkin_table_cell_right_align_numeric_content` | `GherkinFormatConfiguration.TableCellRightAlignNumericContent` | bool | `true` |

### Standard EditorConfig keys adopted by Reqnroll

| `.editorconfig` key | Config property | Type | Default |
|---------------------|-----------------|------|---------|
| `csharp_style_namespace_declarations` | `CSharpCodeGenerationConfiguration.NamespaceDeclarationStyle` | string | `"block_scoped"` |

### Priority rule

`.editorconfig` values override values from `reqnroll.json` / `deveroom.json`. This matches the behaviour documented in the [AutoFormatDocumentCommand feature file](../tests/VisualStudio/Reqnroll.VisualStudio.Specs/Features/Editor/Commands/AutoFormatDocumentCommand.feature): "The settings in .editorconfig file override the setting from the config file."

---

## 3. New Abstractions (`Reqnroll.IdeSupport.Common`)

These interfaces belong in `Common` so they are available to the VS Extension, tests, and any future IDE client without taking a dependency on the LSP server.

### 3.1 `IEditorConfigOptions`

```csharp
// src/Core/Reqnroll.IdeSupport.Common/Configuration/IEditorConfigOptions.cs
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
```

A null-object implementation (`NullEditorConfigOptions`) returns the default value for every key and is used when no `.editorconfig` applies or when EditorConfig lookup is unavailable.

### 3.2 `IEditorConfigOptionsProvider`

```csharp
// src/Core/Reqnroll.IdeSupport.Common/Configuration/IEditorConfigOptionsProvider.cs
namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>
/// Resolves the EditorConfig options that apply to a given file system path.
/// </summary>
public interface IEditorConfigOptionsProvider
{
    /// <summary>
    /// Returns the merged EditorConfig settings that apply to <paramref name="filePath"/>,
    /// following the standard search-upward-from-file-directory semantics.
    /// </summary>
    IEditorConfigOptions GetEditorConfigOptionsByPath(string filePath);
}
```

### 3.3 `EditorConfigOptionsExtensions`

A reflection-based helper that reads every `[EditorConfigSetting]`-annotated property on a config object and overlays the corresponding EditorConfig value. This keeps the overlay logic in one place rather than duplicated in each config class.

```csharp
// src/Core/Reqnroll.IdeSupport.Common/Configuration/EditorConfigOptionsExtensions.cs
namespace Reqnroll.IdeSupport.Common.Configuration;

public static class EditorConfigOptionsExtensions
{
    /// <summary>
    /// For each property on <paramref name="config"/> annotated with
    /// [EditorConfigSetting], reads the corresponding key from <paramref name="options"/>
    /// and replaces the property value if the key is present.
    /// Supported property types: bool, int, string.
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
```

---

## 4. File-System Implementation (`Reqnroll.IdeSupport.LSP.Server`)

### 4.1 EditorConfig file discovery

Standard EditorConfig semantics:

1. Starting from the directory of the target file, collect any `.editorconfig` file present.
2. Walk upward through parent directories, collecting `.editorconfig` files.
3. Stop when a `.editorconfig` with `root = true` is encountered, or the drive root is reached.
4. Apply the collected files in order from most distant to closest (closest overrides distant).
5. Within a single `.editorconfig`, only sections whose glob matches the target file path apply.

### 4.2 `FileSystemEditorConfigOptionsProvider`

Placed in `Reqnroll.IdeSupport.LSP.Server` (or a new `Reqnroll.IdeSupport.Common.IO` project if the VS Extension also needs a file-system reader). It takes `IFileSystemForIDE` so it participates in the same file-system abstraction used throughout the project.

```
src/LSP/Reqnroll.IdeSupport.LSP.Server/Configuration/
    FileSystemEditorConfigOptionsProvider.cs
    FileSystemEditorConfigOptions.cs
```

**Key responsibilities:**

- `GetEditorConfigOptionsByPath(string filePath)` — discovers and parses applicable `.editorconfig` files, merges their matching sections, returns a `FileSystemEditorConfigOptions` wrapping the result.
- Caches parsed `.editorconfig` files by absolute path (keyed on `(path, lastWriteTime)`) so repeated lookups for files in the same directory tree avoid re-parsing.
- Cache is invalidated (entries removed) when a `.editorconfig` change is detected (see §6).

### 4.3 `.editorconfig` parser

A purpose-built parser handles the `.editorconfig` format:

```
# comment
root = true

[*.feature]
gherkin_indent_steps = false
gherkin_table_cell_padding_size = 2

[*.cs]
csharp_style_namespace_declarations = file_scoped:suggestion
```

The parser produces a list of `(GlobPattern, Dictionary<string, string>)` pairs. Key and value comparison is case-insensitive (EditorConfig spec §5).

Section glob matching uses `Microsoft.Extensions.FileSystemGlobbing` (`Matcher`). It handles the EditorConfig glob rules correctly (`*`, `**`, `{a,b}`, `?`, character ranges) and is available on netstandard2.0 with no meaningful transitive cost. The pattern from each `[section]` header is passed directly to `Matcher.AddInclude`; the target file path (relative to the `.editorconfig` file's directory) is matched with `Matcher.Match`.

### 4.4 `GetOption<TResult>` implementation

The returned `IEditorConfigOptions` stores a flat `Dictionary<string, string>` built from the merged, ordered sections. `GetOption<TResult>` does:

```csharp
if (_values.TryGetValue(key, out var raw))
{
    // convert raw string to TResult using TypeDescriptor or Convert
    if (typeof(TResult) == typeof(bool))
        return (TResult)(object)(raw.Equals("true", StringComparison.OrdinalIgnoreCase));
    if (typeof(TResult) == typeof(int) && int.TryParse(raw, out var i))
        return (TResult)(object)i;
    if (typeof(TResult) == typeof(string))
        return (TResult)(object)raw;
}
return defaultValue;
```

---

## 5. Integration with `ProjectScopeDeveroomConfigurationProvider`

### 5.1 No change to `ProjectScopeDeveroomConfigurationProvider`

Because EditorConfig is applied per-document at the point of use (§5.2), `ProjectScopeDeveroomConfigurationProvider` requires no changes. It continues to load JSON/XML sources and return the project baseline configuration unchanged.

### 5.2 Per-document overlay at the point of use

Rather than baking an EditorConfig overlay into `LoadConfiguration` (which only runs once per project), handlers that need EditorConfig values call `IEditorConfigOptionsProvider` directly with the actual document path and overlay the result onto a copy of the project configuration before acting on it.

The pattern is:

```csharp
// Inside a handler (e.g. GherkinFormattingHandler):
var config = _scopeManager.GetConfigurationProviderForUri(uri)
                          .GetConfiguration()
                          .Editor.GherkinFormat
                          .Clone();   // shallow copy — see note below

var editorConfigOptions = _editorConfigProvider.GetEditorConfigOptionsByPath(
    uri.ToLocalPath());             // actual .feature file path

editorConfigOptions.UpdateFromEditorConfig(config);
// use config for this document only
```

`GherkinFormatConfiguration` and `CSharpCodeGenerationConfiguration` must expose a copy constructor or `Clone()` method so handlers can overlay EditorConfig values onto a document-local copy without mutating the cached project configuration.

`ProjectScopeDeveroomConfigurationProvider` itself does **not** call `IEditorConfigOptionsProvider`. It loads only the JSON/XML sources and stores the result as the project baseline. EditorConfig is always a per-request, per-document overlay applied by the handler.

`IEditorConfigOptionsProvider` is injected into handlers directly via DI, alongside `ILspWorkspaceScopeManager`.

### 5.3 DI registration in the LSP server

```csharp
// In the LSP server's DI composition root
services.AddSingleton<IEditorConfigOptionsProvider, FileSystemEditorConfigOptionsProvider>();
```

`IEditorConfigOptionsProvider` is registered as a singleton and injected directly into any handler that needs it. No changes to `LspReqnrollProject`, `ConfigurationProjectSystemExtensions`, or the `IProjectScope.Properties` bag are required.

---

## 6. File Watching

[`WatchedFilesHandler`](../src/LSP/Reqnroll.IdeSupport.LSP.Server/Handlers/ProtocolHandlers/WatchedFilesHandler.cs) registers an additional watcher:

```csharp
new FileSystemWatcher
{
    GlobPattern = "**/.editorconfig",
    Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete
}
```

When a `.editorconfig` change is received, the handler:

1. Identifies the workspace folder(s) that contain the changed file.
2. Calls `_editorConfigProvider.InvalidateCache(changedPath)` to evict the cached parse result for that `.editorconfig` file (and any cached options derived from it).
3. For each `LspReqnrollProject` under the affected folder, reloads configuration (`provider.Reload()`).
4. Publishes `ReqnrollConfigChangedNotification` for each affected project folder, which triggers re-parse of open feature files.

The invalidation path in `FileSystemEditorConfigOptionsProvider.InvalidateCache` removes any cache entry whose `.editorconfig` path equals the changed path. It does not need to know which document paths depend on that `.editorconfig` — the next call to `GetEditorConfigOptionsByPath` for any such document will redo the upward search and re-read the (now-changed) file.

---

## 7. Testing

### Unit tests (`Reqnroll.IdeSupport.Common.Tests`)

- `EditorConfigOptionsExtensionsTests` — verify `UpdateFromEditorConfig` reads each bool/int/string property type, respects defaults when the key is absent, and handles unknown keys gracefully.
- `CSharpCodeGenerationConfigurationTests` (remove the `#if false`) — verify `UpdateFromEditorConfig` for `csharp_style_namespace_declarations`.
- `GherkinFormatConfigurationEditorConfigTests` — verify `UpdateFromEditorConfig` for all 10 `gherkin_*` keys.

### Unit tests (`Reqnroll.IdeSupport.LSP.Server.Tests`)

- `FileSystemEditorConfigOptionsProviderTests` — construct a temporary directory tree with a `.editorconfig` chain, verify values are merged correctly, `root = true` stops the upward walk, and `[*.feature]` sections are applied while `[*.cs]` sections are not.
- `ProjectScopeDeveroomConfigurationProviderEditorConfigTests` — verify that EditorConfig values override JSON config values (i.e., `reqnroll.json` sets `IndentSteps = true`, `.editorconfig` sets `gherkin_indent_steps = false`, resulting config has `IndentSteps = false`).

### Integration / acceptance tests

An acceptance test scenario in the formatting feature file should verify the end-to-end flow: a workspace with a `.editorconfig` containing `gherkin_indent_steps = false` produces formatting output without step indentation even when `reqnroll.json` specifies the default.

---

## 8. File Inventory

| Path | Status | Notes |
|------|--------|-------|
| `src/Core/Reqnroll.IdeSupport.Common/Configuration/EditorConfigSettingAttribute.cs` | Exists | No change |
| `src/Core/Reqnroll.IdeSupport.Common/Configuration/IEditorConfigOptions.cs` | New | |
| `src/Core/Reqnroll.IdeSupport.Common/Configuration/NullEditorConfigOptions.cs` | New | Returns defaultValue for every key |
| `src/Core/Reqnroll.IdeSupport.Common/Configuration/IEditorConfigOptionsProvider.cs` | New | |
| `src/Core/Reqnroll.IdeSupport.Common/Configuration/NullEditorConfigOptionsProvider.cs` | New | Returns NullEditorConfigOptions always |
| `src/Core/Reqnroll.IdeSupport.Common/Configuration/EditorConfigOptionsExtensions.cs` | New | Reflection-based UpdateFromEditorConfig |
| `src/Core/Reqnroll.IdeSupport.Common/ProjectSystem/Configuration/ProjectScopeDeveroomConfigurationProvider.cs` | No change | EditorConfig overlay is now a handler responsibility, not a loader responsibility |
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Configuration/FileSystemEditorConfigOptionsProvider.cs` | New | File-system implementation; singleton DI registration |
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Configuration/FileSystemEditorConfigOptions.cs` | New | Flat key-value dict, type conversion |
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Handlers/ProtocolHandlers/WatchedFilesHandler.cs` | Modify | Add `**/.editorconfig` watcher; call InvalidateCache + Reload on change |
| `tests/VisualStudio/Reqnroll.VisualStudio.VsxStubs/StubEditorConfigOptionsProvider.cs` | Modify | Remove `#if false`; implement using NullEditorConfigOptions |
| `tests/Core/Reqnroll.IdeSupport.Common.Tests/Configuration/CSharpCodeGenerationConfigurationTests.cs` | Modify | Remove `#if false`; implement TestEditorConfigOptions as IEditorConfigOptions |

---

