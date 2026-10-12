package net.reqnroll.idesupport.rider.lsp

import kotlin.test.Test
import kotlin.test.assertEquals

class ReqnrollLspServerDescriptorTest {
    @Test
    fun `resolveLogLevel is Verbose in the dev sandbox`() {
        assertEquals("Verbose", ReqnrollLspServerDescriptor.resolveLogLevel(isDevSandbox = true))
    }

    @Test
    fun `resolveLogLevel is Warning outside the dev sandbox`() {
        assertEquals("Warning", ReqnrollLspServerDescriptor.resolveLogLevel(isDevSandbox = false))
    }
}
