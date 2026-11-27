package net.reqnroll.idesupport.rider.gherkin;

/**
 * Receives the events produced by walking a feature file with the Gherkin parser, in document order.
 * Offsets are into the walked text; token ranges never overlap and never include line breaks.
 */
public interface GherkinSyntaxSink {
    /** A grammar rule (for example {@code Scenario} or {@code Step}) starts before the next token. */
    void startRule(String rule);

    /** The innermost open rule ends after the last token reported. */
    void endRule(String rule);

    /** A token covering {@code [start, end)}. */
    void token(GherkinTokenKind kind, int start, int end);
}
