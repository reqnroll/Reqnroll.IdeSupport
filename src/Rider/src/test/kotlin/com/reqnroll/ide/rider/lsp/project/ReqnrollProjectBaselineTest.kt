package com.reqnroll.ide.rider.lsp.project

import com.jetbrains.rider.model.RdTargetFrameworkId
import com.jetbrains.rider.model.RdVersionInfo
import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ReqnrollProjectBaselineTest {
    @Test
    fun `toClassicMoniker builds the dotted NETCoreApp moniker from version fields`() {
        val tfm = RdTargetFrameworkId(
            RdVersionInfo(9, 0, 0), "net9.0", ".NET 9.0", true, false,
        )

        assertEquals(".NETCoreApp,Version=v9.0", ReqnrollProjectBaseline.toClassicMoniker(tfm))
    }

    @Test
    fun `toClassicMoniker builds the dotted NETFramework moniker from version fields`() {
        val tfm = RdTargetFrameworkId(
            RdVersionInfo(4, 6, 2), "net462", ".NET Framework 4.6.2", false, true,
        )

        assertEquals(".NETFramework,Version=v4.6.2", ReqnrollProjectBaseline.toClassicMoniker(tfm))
    }

    @Test
    fun `toClassicMoniker falls back to shortName when neither flag is set`() {
        val tfm = RdTargetFrameworkId(
            RdVersionInfo(0, 0, 0), "netstandard2.0", ".NET Standard 2.0", false, false,
        )

        assertEquals("netstandard2.0", ReqnrollProjectBaseline.toClassicMoniker(tfm))
    }

    private val sep = File.separator
    private val folder = "${sep}work${sep}App"

    @Test
    fun `isBuildOutput matches the project-root bin and obj folders and their contents`() {
        assertTrue(ReqnrollProjectBaseline.isBuildOutput("$folder${sep}obj", folder))
        assertTrue(ReqnrollProjectBaseline.isBuildOutput("$folder${sep}bin", folder))
        assertTrue(ReqnrollProjectBaseline.isBuildOutput("$folder${sep}obj${sep}Debug${sep}net8.0${sep}App.AssemblyInfo.cs", folder))
        assertTrue(ReqnrollProjectBaseline.isBuildOutput("$folder${sep}OBJ${sep}x.cs", folder))
    }

    @Test
    fun `isBuildOutput ignores nested bin folders and look-alike names`() {
        assertFalse(ReqnrollProjectBaseline.isBuildOutput("$folder${sep}Features${sep}bin${sep}A.feature", folder))
        assertFalse(ReqnrollProjectBaseline.isBuildOutput("$folder${sep}objects${sep}A.cs", folder))
        assertFalse(ReqnrollProjectBaseline.isBuildOutput("$folder${sep}Steps.cs", folder))
    }
}
