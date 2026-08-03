Feature: Corpus feature 041
  Synthetic benchmark feature 041 for performance verification.

  @hookscope
  Scenario: Scenario 041-0
    Given precondition 0 is met
    When feature 44 is enabled
    When action 0 is performed
    When undefined step 41-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 041-1
    Given precondition 1 is met
    When feature 45 is enabled
    When action 1 is performed
    When undefined step 41-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 041-2
    Given precondition 2 is met
    When feature 46 is enabled
    When action 2 is performed
    When undefined step 41-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 041-3
    Given precondition 3 is met
    When feature 47 is enabled
    When action 3 is performed
    When undefined step 41-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 041
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

