package net.reqnroll.idesupport.rider

import com.intellij.openapi.fileTypes.LanguageFileType
import javax.swing.Icon

/** File type for `.feature` files, associating them with [ReqnrollFeatureLanguage]. Registered via `plugin.xml`'s `fileType` extension. */
object ReqnrollFeatureFileType : LanguageFileType(ReqnrollFeatureLanguage) {
    override fun getName() = "Reqnroll Feature"
    override fun getDescription() = "Reqnroll/Gherkin feature file"
    override fun getDefaultExtension() = "feature"
    override fun getIcon(): Icon? = null
}
