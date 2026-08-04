package com.reqnroll.ide.rider

import com.intellij.lang.Language

/**
 * The Gherkin/`.feature` file language, identified purely by ID ("Gherkin", matching VS's and VS
 * Code's identifiers for the same concept — issue #290) — there's no grammar/`ParserDefinition`
 * registered; all language-aware behavior (coloring, diagnostics, navigation, etc.) comes from
 * the LSP server via [com.reqnroll.ide.rider.lsp.ReqnrollLspServerDescriptor]. The file type's own
 * user-visible display name ([ReqnrollFeatureFileType.getName]) stays "Reqnroll Feature" — this
 * ID is purely an internal registration key, not shown to users.
 *
 * `Language`'s id registry is a single process-wide namespace and throws at construction if the
 * id is already taken. JetBrains' bundled Cucumber/Gherkin support plugin (if installed alongside
 * this one) registers its own Gherkin language, conventionally under this exact same "Gherkin"
 * id — this hasn't been verified live (no cached IntelliJ Platform/Rider SDK available to inspect,
 * no GUI to run `runIde` with that plugin enabled). If the plugin fails to load with a duplicate-id
 * error after this change, that collision is why — see issue #290's PR discussion.
 */
object ReqnrollFeatureLanguage : Language("Gherkin")
