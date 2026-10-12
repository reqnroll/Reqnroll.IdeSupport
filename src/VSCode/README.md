# Reqnroll Extension for VS Code (Preview)

Gherkin and [Reqnroll](https://reqnroll.net) support for VS Code: syntax highlighting, navigation, completion, diagnostics and more for your `.feature` files and the C# step definitions behind them.

> **Preview — we want your feedback.**
> This is the new, consolidated **Reqnroll IDE Support**: one shared language server with a native extension for **Visual Studio**, **VS Code** and **Rider**, replacing the separate legacy extensions. It is in a preview period and we would like to hear how it works for you.
>
> * If you hit a severe problem, the earlier extensions are still available: you can disable this one and switch back at any time. (Only keep one enabled for the same `.feature` files.)
> * Please report bugs and ideas on [GitHub Issues](https://github.com/reqnroll/Reqnroll.IdeSupport/issues). Include your VS Code and extension versions and, if you can, the log output.
> * To collect diagnostic information, set `reqnroll.trace.server` to `verbose` and reload the window. See [where the logs are and how to change the log level](https://github.com/reqnroll/Reqnroll.IdeSupport/blob/main/docs/site/ide-support/troubleshooting.md#where-are-the-logs-and-how-do-i-change-the-log-level).

## Features

* Syntax highlighting for Gherkin feature files
* Errors and warnings for Gherkin syntax and for steps that have no matching step definition (or more than one)
* Keyword, step and tag completion
* Document and table formatting
* Comment / uncomment
* Code folding and document outline
* Go to step definition and hook navigation
* Find step definition usages and find unused step definitions
* Define missing steps from a quick fix
* Rename a step across feature files and step definitions
* Code Lens with step usage counts and hook matches
* Inlay hints showing the step definition a step is bound to

See the [feature overview](https://github.com/reqnroll/Reqnroll.IdeSupport/blob/main/docs/site/ide-support/feature-overview.md) for what is available in each IDE.

## Requirements

* VS Code 1.96 or later
* A .NET project that uses Reqnroll.
* Optional: the [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit) for running tests from VS Code. Reqnroll does not depend on it. Only `reqnroll.testOutcomes.enabled` interacts with it, through the `dotnet.unitTests.runSettingsPath` setting that the C# extension provides.

## Settings

| Setting | What it does |
|---|---|
| `reqnroll.trace.server` | Turns on troubleshooting output. Use it when you are asked for logs to report a problem. |
| `reqnroll.protocolLogLevel` | How much low-level technical detail the language server logs. Leave it at the default unless asked. |
| `reqnroll.testOutcomes.enabled` | Show per-example results for Scenario Outline runs and failed-step details after running tests with C# Dev Kit. Off by default; has no effect if the `dotnet.unitTests.runSettingsPath` setting is not available. |

## Links

* [Reqnroll](https://reqnroll.net)
* [Source code and issue tracker](https://github.com/reqnroll/Reqnroll.IdeSupport)
* [Troubleshooting and FAQ](https://github.com/reqnroll/Reqnroll.IdeSupport/blob/main/docs/site/ide-support/troubleshooting.md)
