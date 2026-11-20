package net.reqnroll.idesupport.rider.debugger

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class StepDefinitionBodyLocatorTest {
    private fun bodyLine(source: String, name: String): Int? =
        StepDefinitionBodyLocator.bodyStartLine(source, source.indexOf(name))

    @Test
    fun `a block body starts at the line of its opening brace`() {
        val source = """
            [Given("the client added {int} pcs of {string} to the basket")]
            public void GivenTheClientAddedPcsOfToTheBasket(int quantity, string product)
            {
                _basket.Add(product, quantity);
            }
        """.trimIndent()
        assertEquals(2, bodyLine(source, "GivenTheClientAdded"))
    }

    @Test
    fun `a brace on the signature line is that line`() {
        val source = "public async Task WhenPrepared() {\n    await _basket.PrepareAsync();\n}"
        assertEquals(0, bodyLine(source, "WhenPrepared"))
    }

    @Test
    fun `an expression body starts where the expression starts`() {
        val sameLine = "public void Then(decimal price) => Assert.AreEqual(price, _basket.Price);"
        assertEquals(0, bodyLine(sameLine, "Then"))
        val nextLine = "public void Then(decimal price) =>\n    Assert.AreEqual(price, _basket.Price);"
        assertEquals(1, bodyLine(nextLine, "Then"))
    }

    @Test
    fun `braces and arrows inside the parameter list are skipped`() {
        val source = listOf(
            "public void Given(",
            "    string text = \"{not the body} => nor \\\"this\\\"\",",
            "    char c = '{',",
            "    string path = @\"C:\\{dir}\\\"\"quoted\"\"\",",
            "    Func<int, int>? f = null /* { */) // {",
            "{",
            "    Use(text);",
            "}",
        ).joinToString("\n")
        assertEquals(5, bodyLine(source, "Given"))
    }

    @Test
    fun `a generic constraint after the parameters does not end the search`() {
        val source = "public void Given<T>(T value)\n    where T : new()\n{\n}"
        assertEquals(2, bodyLine(source, "Given"))
    }

    @Test
    fun `a declaration without a body has no body line`() {
        assertNull(bodyLine("public abstract void Given(int x);\nvoid Other() { }", "Given"))
        assertNull(bodyLine("public void Given(int x)", "Given"))
    }

    @Test
    fun `an offset outside the text has no body line`() {
        assertNull(StepDefinitionBodyLocator.bodyStartLine("void A() { }", 99))
        assertNull(StepDefinitionBodyLocator.bodyStartLine("void A() { }", -1))
    }

    @Test
    fun `CRLF line endings count lines the same`() {
        val source = "public void Given()\r\n{\r\n}\r\n"
        assertEquals(1, bodyLine(source, "Given"))
    }
}
