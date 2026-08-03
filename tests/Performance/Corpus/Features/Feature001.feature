Feature: Corpus feature 001
  Synthetic benchmark feature 001 for performance verification.

  @hookscope
  Scenario: Scenario 001-0
    Given precondition 0 is met
    When feature 4 is enabled
    When action 0 is performed
    When undefined step 1-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 001-1
    Given precondition 1 is met
    When feature 5 is enabled
    When action 1 is performed
    When undefined step 1-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 001-2
    Given precondition 2 is met
    When feature 6 is enabled
    When action 2 is performed
    When undefined step 1-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 001-3
    Given precondition 3 is met
    When feature 7 is enabled
    When action 3 is performed
    When undefined step 1-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 001
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

