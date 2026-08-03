Feature: Corpus feature 004
  Synthetic benchmark feature 004 for performance verification.

  @hookscope
  Scenario: Scenario 004-0
    Given precondition 0 is met
    When feature 16 is enabled
    When action 0 is performed
    When undefined step 4-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 004-1
    Given precondition 1 is met
    When feature 17 is enabled
    When action 1 is performed
    When undefined step 4-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 004-2
    Given precondition 2 is met
    When feature 18 is enabled
    When action 2 is performed
    When undefined step 4-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 004-3
    Given precondition 3 is met
    When feature 19 is enabled
    When action 3 is performed
    When undefined step 4-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 004
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

