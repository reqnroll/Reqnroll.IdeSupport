package net.reqnroll.idesupport.rider.psi

import com.intellij.lang.Commenter

/**
 * Tells the platform that `#` starts a line comment in a feature file. Gherkin has no block comments.
 *
 * The Comment / Uncomment action itself is still `ReqnrollCommentByLineCommentAction`, which has the language
 * server do the toggling; this only supplies the prefix to the platform features that read it.
 */
class ReqnrollFeatureCommenter : Commenter {
    override fun getLineCommentPrefix(): String = "# "

    override fun getBlockCommentPrefix(): String? = null

    override fun getBlockCommentSuffix(): String? = null

    override fun getCommentedBlockCommentPrefix(): String? = null

    override fun getCommentedBlockCommentSuffix(): String? = null
}
