package net.reqnroll.idesupport.rider.gherkin;

/** The kinds of token the Gherkin syntax walker reports, one per highlighted span of a line. */
public enum GherkinTokenKind {
    /** A {@code # language: xx} header line. */
    LANGUAGE_HEADER,
    /** A {@code # ...} comment line. */
    COMMENT,
    /** One {@code @tag}. */
    TAG,
    /** A structural keyword and its colon: Feature, Rule, Background, Scenario, Examples. */
    KEYWORD,
    /** The name after a structural keyword. */
    NAME,
    /** A step keyword: Given, When, Then, And, But or {@code *}. */
    STEP_KEYWORD,
    /** Step text outside placeholders. */
    STEP_TEXT,
    /** A {@code <placeholder>} inside step text, a table cell or doc-string content. */
    PLACEHOLDER,
    /** A {@code |} between table cells. */
    PIPE,
    /** The text of one table cell, outside placeholders. */
    CELL,
    /** A {@code """} or {@code ```} doc-string delimiter. */
    DOC_STRING_DELIMITER,
    /** The content type after an opening doc-string delimiter. */
    DOC_STRING_TYPE,
    /** A line of doc-string content, outside placeholders. */
    DOC_STRING_CONTENT,
    /** A line of free-form description text. */
    DESCRIPTION
}
