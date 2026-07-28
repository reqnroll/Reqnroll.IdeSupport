package com.reqnroll.ide.rider

/**
 * Case-insensitive `.feature`/`.cs` extension checks, factored out as pure functions (taking the
 * extension string rather than a `VirtualFile`) so they're unit-testable without a platform
 * fixture. `VirtualFile.extension` reflects a file's on-disk casing verbatim -- on a
 * case-insensitive filesystem (Windows, default macOS) a file can easily end up named
 * `Steps.CS`/`Login.Feature` without anyone noticing, so every call site deciding whether a file
 * is "a Reqnroll file" needs to compare case-insensitively, matching how `ProjectFileRole.classify`
 * already does it for full paths. See issue #358: several call sites used case-sensitive `==`/`!=`
 * instead, silently disagreeing with the rest of the plugin about which files it applies to.
 */
internal fun isFeatureExtension(extension: String?): Boolean = extension.equals("feature", ignoreCase = true)

internal fun isCsExtension(extension: String?): Boolean = extension.equals("cs", ignoreCase = true)
