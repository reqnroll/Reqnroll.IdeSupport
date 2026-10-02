Feature: Comment / Uncomment (F13)

workspace/executeCommand with reqnroll.toggleComment toggles # comments
on the selected line(s) of a .feature file, applying the edit via workspace/applyEdit request.
An optional fourth argument ("toggle", "comment" or "uncomment") forces the direction, matching
Visual Studio's Comment Selection / Uncomment Selection commands (issue #747).

Background:
    Given the LSP server is started

# ── Toggle ON (comment uncommented lines) ─────────────────────────────────────

Scenario: Toggle comment adds hash to a single uncommented line
    When the feature file "SingleLine.feature" is opened with
        """
        Feature: Calculator
        """
    And the toggle comment command is executed for "SingleLine.feature" on lines 0 to 0
    Then a workspace/applyEdit request is received
    And the edit replaces line 0 with "# Feature: Calculator"

Scenario: Toggle comment adds hash to multiple lines
    When the feature file "MultiLines.feature" is opened with
        """
        Feature: F
        Scenario: S
            Given a step
        """
    And the toggle comment command is executed for "MultiLines.feature" on lines 0 to 2
    Then the edit replaces line 0 with "# Feature: F"
    And the edit replaces line 1 with "# Scenario: S"
    And the edit replaces line 2 with "#     Given a step"

# ── Toggle OFF (uncomment all-commented lines) ────────────────────────────────

Scenario: Toggle comment removes hash from a single commented line
    When the feature file "Commented.feature" is opened with
        """
        # Feature: Calculator
        """
    And the toggle comment command is executed for "Commented.feature" on lines 0 to 0
    Then the edit replaces line 0 with "Feature: Calculator"

Scenario: Toggle comment removes hash from multiple commented lines
    When the feature file "MultiCommented.feature" is opened with
        """
        # Feature: F
        # Scenario: S
        #     Given a step
        """
    And the toggle comment command is executed for "MultiCommented.feature" on lines 0 to 2
    Then the edit replaces line 0 with "Feature: F"
    And the edit replaces line 1 with "Scenario: S"
    And the edit replaces line 2 with "    Given a step"

# ── Partial range ─────────────────────────────────────────────────────────────

Scenario: Only lines in the specified range are toggled
    When the feature file "PartialRange.feature" is opened with
        """
        Feature: F
        Scenario: S
            Given a step
        """
    And the toggle comment command is executed for "PartialRange.feature" on lines 1 to 1
    Then the edit replaces line 1 with "# Scenario: S"

# ── Mixed selection: not all commented → add hashes ──────────────────────────

Scenario: Toggle on a selection with mixed commented and uncommented lines adds hashes to all
    When the feature file "Mixed.feature" is opened with
        """
        # Feature: F
        Scenario: S
        """
    And the toggle comment command is executed for "Mixed.feature" on lines 0 to 1
    Then the edit replaces line 0 with "# # Feature: F"
    And the edit replaces line 1 with "# Scenario: S"

# ── Indented lines ────────────────────────────────────────────────────────────

Scenario: Toggle comment on indented step lines adds hash at column 0
    When the feature file "Indented.feature" is opened with
        """
        Feature: F
        Scenario: S
            Given a step
            When another step
        """
    And the toggle comment command is executed for "Indented.feature" on lines 2 to 3
    Then the edit replaces line 2 with "#     Given a step"
    And the edit replaces line 3 with "#     When another step"

# ── Explicit mode (VS Comment Selection / Uncomment Selection) ────────────────

Scenario: Comment mode adds a hash even when every line is already commented
    When the feature file "CommentMode.feature" is opened with
        """
        # Feature: F
        # Scenario: S
        """
    And the toggle comment command is executed for "CommentMode.feature" on lines 0 to 1 with mode "comment"
    Then the edit replaces line 0 with "# # Feature: F"
    And the edit replaces line 1 with "# # Scenario: S"

Scenario: Uncomment mode removes hashes only from commented lines
    When the feature file "UncommentMode.feature" is opened with
        """
        # Feature: F
        Scenario: S
        """
    And the toggle comment command is executed for "UncommentMode.feature" on lines 0 to 1 with mode "uncomment"
    Then the edit replaces line 0 with "Feature: F"
    And the edit does not change line 1

Scenario: Toggle mode behaves like the command without a mode
    When the feature file "ToggleMode.feature" is opened with
        """
        # Feature: Calculator
        """
    And the toggle comment command is executed for "ToggleMode.feature" on lines 0 to 0 with mode "toggle"
    Then the edit replaces line 0 with "Feature: Calculator"
