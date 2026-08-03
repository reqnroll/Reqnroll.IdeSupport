Feature: Corpus feature 024
  Synthetic benchmark feature 024 for performance verification.

  @hookscope
  Scenario: Scenario 024-0
    Given precondition 0 is met
    When feature 36 is enabled
    When action 0 is performed
    When undefined step 24-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 024-1
    Given precondition 1 is met
    When feature 37 is enabled
    When action 1 is performed
    When undefined step 24-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 024-2
    Given precondition 2 is met
    When feature 38 is enabled
    When action 2 is performed
    When undefined step 24-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 024-3
    Given precondition 3 is met
    When feature 39 is enabled
    When action 3 is performed
    When undefined step 24-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 024
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

