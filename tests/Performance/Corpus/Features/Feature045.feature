Feature: Corpus feature 045
  Synthetic benchmark feature 045 for performance verification.

  @hookscope
  Scenario: Scenario 045-0
    Given precondition 0 is met
    When feature 0 is enabled
    When action 0 is performed
    When undefined step 45-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 045-1
    Given precondition 1 is met
    When feature 1 is enabled
    When action 1 is performed
    When undefined step 45-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 045-2
    Given precondition 2 is met
    When feature 2 is enabled
    When action 2 is performed
    When undefined step 45-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 045-3
    Given precondition 3 is met
    When feature 3 is enabled
    When action 3 is performed
    When undefined step 45-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 045
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

