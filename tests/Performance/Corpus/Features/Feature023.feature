Feature: Corpus feature 023
  Synthetic benchmark feature 023 for performance verification.

  @hookscope
  Scenario: Scenario 023-0
    Given precondition 0 is met
    When feature 32 is enabled
    When action 0 is performed
    When undefined step 23-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 023-1
    Given precondition 1 is met
    When feature 33 is enabled
    When action 1 is performed
    When undefined step 23-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 023-2
    Given precondition 2 is met
    When feature 34 is enabled
    When action 2 is performed
    When undefined step 23-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 023-3
    Given precondition 3 is met
    When feature 35 is enabled
    When action 3 is performed
    When undefined step 23-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 023
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

