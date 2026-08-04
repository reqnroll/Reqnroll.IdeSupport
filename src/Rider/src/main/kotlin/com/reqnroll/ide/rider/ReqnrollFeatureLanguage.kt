package com.reqnroll.ide.rider

import com.intellij.lang.Language

/**
 * The Gherkin/`.feature` file language, identified purely by ID ("gherkin", matching VS's and VS
 * Code's identifiers for the same concept — issue #290) — there's no grammar/`ParserDefinition`
 * registered; all language-aware behavior (coloring, diagnostics, navigation, etc.) comes from
 * the LSP server via [com.reqnroll.ide.rider.lsp.ReqnrollLspServerDescriptor]. The file type's own
 * user-visible display name ([ReqnrollFeatureFileType.getName]) stays "Reqnroll Feature" — this
 * ID is purely an internal registration key, not shown to users.
 */
object ReqnrollFeatureLanguage : Language("gherkin")
