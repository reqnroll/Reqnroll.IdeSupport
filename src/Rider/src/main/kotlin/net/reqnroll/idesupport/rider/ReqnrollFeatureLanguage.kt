package net.reqnroll.idesupport.rider

import com.intellij.lang.Language

/**
 * The Gherkin/`.feature` file language, identified purely by ID ("Gherkin", matching VS's
 * identifier for the same concept; VS Code keeps its pre-existing lowercase "gherkin"
 * per that platform's own naming convention) — there's no grammar/`ParserDefinition` registered;
 * all language-aware behavior (coloring, diagnostics, navigation, etc.) comes from the LSP server
 * via [net.reqnroll.idesupport.rider.lsp.ReqnrollLspServerDescriptor]. The file type's own user-visible
 * display name ([ReqnrollFeatureFileType.getName]) stays "Reqnroll Feature" — this ID is purely an
 * internal registration key, not shown to users.
 *
 * `Language`'s id registry is a single process-wide namespace and throws at construction if the
 * id is already taken — confirmed live (manual `runIde` smoke test) that this "Gherkin"
 * id does not collide with anything in a standard Rider install.
 */
object ReqnrollFeatureLanguage : Language("Gherkin")
