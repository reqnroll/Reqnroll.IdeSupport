Feature: Corpus feature 005
  Synthetic benchmark feature 005 for performance verification.

  @hookscope
  Scenario: Scenario 005-0
    Given precondition 0 is met
    When feature 20 is enabled
    When action 0 is performed
    When undefined step 5-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 005-1
    Given precondition 1 is met
    When feature 21 is enabled
    When action 1 is performed
    When undefined step 5-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 005-2
    Given precondition 2 is met
    When feature 22 is enabled
    When action 2 is performed
    When undefined step 5-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 005-3
    Given precondition 3 is met
    When feature 23 is enabled
    When action 3 is performed
    When undefined step 5-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 005
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

