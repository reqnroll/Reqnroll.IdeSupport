Feature: Corpus feature 025
  Synthetic benchmark feature 025 for performance verification.

  @hookscope
  Scenario: Scenario 025-0
    Given precondition 0 is met
    When feature 40 is enabled
    When action 0 is performed
    When undefined step 25-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 025-1
    Given precondition 1 is met
    When feature 41 is enabled
    When action 1 is performed
    When undefined step 25-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 025-2
    Given precondition 2 is met
    When feature 42 is enabled
    When action 2 is performed
    When undefined step 25-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 025-3
    Given precondition 3 is met
    When feature 43 is enabled
    When action 3 is performed
    When undefined step 25-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 025
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

