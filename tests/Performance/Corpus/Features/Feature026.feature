Feature: Corpus feature 026
  Synthetic benchmark feature 026 for performance verification.

  @hookscope
  Scenario: Scenario 026-0
    Given precondition 0 is met
    When feature 44 is enabled
    When action 0 is performed
    When undefined step 26-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 026-1
    Given precondition 1 is met
    When feature 45 is enabled
    When action 1 is performed
    When undefined step 26-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 026-2
    Given precondition 2 is met
    When feature 46 is enabled
    When action 2 is performed
    When undefined step 26-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 026-3
    Given precondition 3 is met
    When feature 47 is enabled
    When action 3 is performed
    When undefined step 26-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 026
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

