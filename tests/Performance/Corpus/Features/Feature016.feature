Feature: Corpus feature 016
  Synthetic benchmark feature 016 for performance verification.

  @hookscope
  Scenario: Scenario 016-0
    Given precondition 0 is met
    When feature 4 is enabled
    When action 0 is performed
    When undefined step 16-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 016-1
    Given precondition 1 is met
    When feature 5 is enabled
    When action 1 is performed
    When undefined step 16-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 016-2
    Given precondition 2 is met
    When feature 6 is enabled
    When action 2 is performed
    When undefined step 16-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 016-3
    Given precondition 3 is met
    When feature 7 is enabled
    When action 3 is performed
    When undefined step 16-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 016
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

