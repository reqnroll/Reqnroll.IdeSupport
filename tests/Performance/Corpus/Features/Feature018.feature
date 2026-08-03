Feature: Corpus feature 018
  Synthetic benchmark feature 018 for performance verification.

  @hookscope
  Scenario: Scenario 018-0
    Given precondition 0 is met
    When feature 12 is enabled
    When action 0 is performed
    When undefined step 18-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 018-1
    Given precondition 1 is met
    When feature 13 is enabled
    When action 1 is performed
    When undefined step 18-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 018-2
    Given precondition 2 is met
    When feature 14 is enabled
    When action 2 is performed
    When undefined step 18-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 018-3
    Given precondition 3 is met
    When feature 15 is enabled
    When action 3 is performed
    When undefined step 18-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 018
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

