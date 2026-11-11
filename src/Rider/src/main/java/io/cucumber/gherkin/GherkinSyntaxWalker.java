package io.cucumber.gherkin;

import net.reqnroll.idesupport.rider.gherkin.GherkinSyntaxSink;
import net.reqnroll.idesupport.rider.gherkin.GherkinTokenKind;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.List;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Walks a feature file with the Cucumber Gherkin parser and reports its structure and the span of each token
 * to a {@link GherkinSyntaxSink}.
 *
 * <p>This class lives in the library's own package because the parser, its builder callback interface, the
 * token matcher and the token type are all package-private. Nothing package-private is exposed: the only public
 * surface is {@link #walk}, which speaks in {@link GherkinSyntaxSink} terms.
 *
 * <p>The walk never fails. The library collects syntax errors while it keeps parsing and throws them at the end
 * (or throws at an unexpected end of file), and every callback made up to that point has already been reported.
 * Rules still open when parsing stops are closed, so the reported structure is always balanced.
 */
public final class GherkinSyntaxWalker {
    private static final String FALLBACK_LANGUAGE = "en";

    private GherkinSyntaxWalker() {
    }

    /**
     * Reports the structure of {@code text} to {@code sink}.
     *
     * @param defaultLanguage the dialect used until a {@code # language:} header says otherwise
     */
    public static void walk(String text, String defaultLanguage, GherkinSyntaxSink sink) {
        Walker walker = new Walker(text, sink);
        try {
            new Parser<Object>(walker).parse(new TokenScanner(text), matcherFor(defaultLanguage), "");
        } catch (RuntimeException expected) {
            // Syntax errors and an unexpected end of file surface here; what was built before is kept.
        }
        walker.closeOpenRules();
    }

    private static Parser.TokenMatcher matcherFor(String defaultLanguage) {
        try {
            return new GherkinTokenMatcher(defaultLanguage);
        } catch (RuntimeException unknownLanguage) {
            return new GherkinTokenMatcher(FALLBACK_LANGUAGE);
        }
    }

    private static final class Walker implements Parser.Builder<Object> {
        private static final Pattern PLACEHOLDER = Pattern.compile("<[^<>]+>");

        private final String text;
        private final GherkinSyntaxSink sink;
        private final List<int[]> lines = new ArrayList<>();
        private final Deque<String> openRules = new ArrayDeque<>();

        Walker(String text, GherkinSyntaxSink sink) {
            this.text = text;
            this.sink = sink;
            indexLines();
        }

        private void indexLines() {
            int length = text.length();
            int start = 0;
            int i = 0;
            while (i < length) {
                char c = text.charAt(i);
                if (c == '\n' || c == '\r') {
                    lines.add(new int[] {start, i});
                    if (c == '\r' && i + 1 < length && text.charAt(i + 1) == '\n') {
                        i++;
                    }
                    start = i + 1;
                }
                i++;
            }
            if (start < length) {
                lines.add(new int[] {start, length});
            }
        }

        void closeOpenRules() {
            while (!openRules.isEmpty()) {
                sink.endRule(openRules.pop());
            }
        }

        private static boolean isReported(Parser.RuleType rule) {
            String name = rule.name();
            return !name.startsWith("_") && rule != Parser.RuleType.None && rule != Parser.RuleType.GherkinDocument;
        }

        @Override
        public void startRule(Parser.RuleType rule) {
            if (!isReported(rule)) {
                return;
            }
            openRules.push(rule.name());
            sink.startRule(rule.name());
        }

        @Override
        public void endRule(Parser.RuleType rule) {
            if (!isReported(rule) || openRules.isEmpty()) {
                return;
            }
            sink.endRule(openRules.pop());
        }

        @Override
        public Object getResult() {
            return null;
        }

        @Override
        public void reset(String uri) {
        }

        @Override
        public void build(Token token) {
            if (token.isEOF() || token.matchedType == null) {
                return;
            }
            int lineIndex = token.location.getLine() - 1;
            if (lineIndex < 0 || lineIndex >= lines.size()) {
                return;
            }
            int lineStart = lines.get(lineIndex)[0];
            int lineEnd = lines.get(lineIndex)[1];
            int contentStart = skipWhitespace(lineStart, lineEnd);
            int contentEnd = trimEnd(contentStart, lineEnd);
            if (contentStart >= contentEnd) {
                return;
            }

            switch (token.matchedType) {
                case Comment:
                    sink.token(GherkinTokenKind.COMMENT, contentStart, contentEnd);
                    break;
                case Language:
                    sink.token(GherkinTokenKind.LANGUAGE_HEADER, contentStart, contentEnd);
                    break;
                case TagLine:
                    tags(token, lineStart, contentEnd);
                    break;
                case FeatureLine:
                case RuleLine:
                case BackgroundLine:
                case ScenarioLine:
                case ExamplesLine:
                    titleLine(token, contentStart, contentEnd, lineEnd);
                    break;
                case StepLine:
                    stepLine(token, contentStart, contentEnd, lineEnd);
                    break;
                case DocStringSeparator:
                    docStringSeparator(contentStart, contentEnd);
                    break;
                case TableRow:
                    tableRow(contentStart, contentEnd);
                    break;
                case Other:
                    if (openRules.contains("DocString")) {
                        withPlaceholders(GherkinTokenKind.DOC_STRING_CONTENT, contentStart, contentEnd);
                    } else {
                        sink.token(GherkinTokenKind.DESCRIPTION, contentStart, contentEnd);
                    }
                    break;
                default:
                    break;
            }
        }

        private void tags(Token token, int lineStart, int contentEnd) {
            int previousEnd = lineStart;
            for (LineSpan span : token.matchedItems) {
                int start = lineStart + span.column - Locations.COLUMN_OFFSET;
                int end = Math.min(start + span.text.length(), contentEnd);
                if (start >= previousEnd && start < end) {
                    sink.token(GherkinTokenKind.TAG, start, end);
                    previousEnd = end;
                }
            }
            int commentStart = skipWhitespace(previousEnd, contentEnd);
            if (commentStart < contentEnd && text.charAt(commentStart) == '#') {
                sink.token(GherkinTokenKind.COMMENT, commentStart, contentEnd);
            }
        }

        private void titleLine(Token token, int contentStart, int contentEnd, int lineEnd) {
            int keywordEnd = Math.min(contentStart + token.matchedKeyword.length(), contentEnd);
            if (keywordEnd < contentEnd && text.charAt(keywordEnd) == ':') {
                keywordEnd++;
            }
            sink.token(GherkinTokenKind.KEYWORD, contentStart, keywordEnd);
            int nameStart = skipWhitespace(keywordEnd, contentEnd);
            if (nameStart < contentEnd) {
                sink.token(GherkinTokenKind.NAME, nameStart, contentEnd);
            }
        }

        private void stepLine(Token token, int contentStart, int contentEnd, int lineEnd) {
            String keyword = token.matchedKeyword;
            int keywordEnd = Math.min(contentStart + keyword.length(), contentEnd);
            int keywordTokenEnd = trimEnd(contentStart, keywordEnd);
            if (keywordTokenEnd > contentStart) {
                sink.token(GherkinTokenKind.STEP_KEYWORD, contentStart, keywordTokenEnd);
            }
            int textStart = skipWhitespace(keywordEnd, contentEnd);
            if (textStart < contentEnd) {
                withPlaceholders(GherkinTokenKind.STEP_TEXT, textStart, contentEnd);
            }
        }

        private void docStringSeparator(int contentStart, int contentEnd) {
            int delimiterEnd = Math.min(contentStart + 3, contentEnd);
            sink.token(GherkinTokenKind.DOC_STRING_DELIMITER, contentStart, delimiterEnd);
            int typeStart = skipWhitespace(delimiterEnd, contentEnd);
            if (typeStart < contentEnd) {
                sink.token(GherkinTokenKind.DOC_STRING_TYPE, typeStart, contentEnd);
            }
        }

        private void tableRow(int contentStart, int contentEnd) {
            boolean placeholders = !openRules.contains("ExamplesTable");
            int cellStart = -1;
            int i = contentStart;
            while (i < contentEnd) {
                char c = text.charAt(i);
                if (c == '\\' && i + 1 < contentEnd) {
                    i += 2;
                    continue;
                }
                if (c == '|') {
                    if (cellStart >= 0) {
                        cell(cellStart, i, placeholders);
                    }
                    sink.token(GherkinTokenKind.PIPE, i, i + 1);
                    cellStart = i + 1;
                }
                i++;
            }
            if (cellStart >= 0 && cellStart < contentEnd) {
                cell(cellStart, contentEnd, placeholders);
            }
        }

        private void cell(int start, int end, boolean placeholders) {
            int cellStart = skipWhitespace(start, end);
            int cellEnd = trimEnd(cellStart, end);
            if (cellStart >= cellEnd) {
                return;
            }
            if (placeholders) {
                withPlaceholders(GherkinTokenKind.CELL, cellStart, cellEnd);
            } else {
                sink.token(GherkinTokenKind.CELL, cellStart, cellEnd);
            }
        }

        private void withPlaceholders(GherkinTokenKind kind, int start, int end) {
            Matcher matcher = PLACEHOLDER.matcher(text).region(start, end);
            int position = start;
            while (matcher.find()) {
                if (matcher.start() > position) {
                    sink.token(kind, position, matcher.start());
                }
                sink.token(GherkinTokenKind.PLACEHOLDER, matcher.start(), matcher.end());
                position = matcher.end();
            }
            if (position < end) {
                sink.token(kind, position, end);
            }
        }

        private int skipWhitespace(int from, int to) {
            int i = from;
            while (i < to && Character.isWhitespace(text.charAt(i))) {
                i++;
            }
            return i;
        }

        private int trimEnd(int from, int to) {
            int i = to;
            while (i > from && Character.isWhitespace(text.charAt(i - 1))) {
                i--;
            }
            return i;
        }
    }
}
