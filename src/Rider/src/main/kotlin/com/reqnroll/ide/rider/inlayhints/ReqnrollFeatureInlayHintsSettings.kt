package com.reqnroll.ide.rider.inlayhints

import com.intellij.ide.util.PropertiesComponent

/**
 * Persisted on/off switch for [ReqnrollFeatureInlayHintsController]. A plain
 * [PropertiesComponent] flag rather than IntelliJ's declarative inlay-hints settings page: that
 * page is keyed by a registered [com.intellij.codeInsight.hints.declarative.InlayHintsProvider],
 * which — per [ReqnrollFeatureInlayHintsController]'s doc comment — can't be used here because
 * `.feature` files have no PSI language for it to dispatch on. [ReqnrollToggleFeatureInlayHintsAction]
 * is the only UI surface for this flag.
 */
object ReqnrollFeatureInlayHintsSettings {
    private const val KEY = "Reqnroll.FeatureInlayHints.Enabled"

    var isEnabled: Boolean
        get() = PropertiesComponent.getInstance().getBoolean(KEY, true)
        set(value) = PropertiesComponent.getInstance().setValue(KEY, value, true)
}
