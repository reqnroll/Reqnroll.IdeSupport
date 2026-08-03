Feature: Corpus feature 007
  Synthetic benchmark feature 007 for performance verification.

  @hookscope
  Scenario: Scenario 007-0
    Given precondition 0 is met
    When feature 28 is enabled
    When action 0 is performed
    When undefined step 7-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 007-1
    Given precondition 1 is met
    When feature 29 is enabled
    When action 1 is performed
    When undefined step 7-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 007-2
    Given precondition 2 is met
    When feature 30 is enabled
    When action 2 is performed
    When undefined step 7-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 007-3
    Given precondition 3 is met
    When feature 31 is enabled
    When action 3 is performed
    When undefined step 7-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 007
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

