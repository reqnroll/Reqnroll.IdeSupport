Feature: Corpus feature 017
  Synthetic benchmark feature 017 for performance verification.

  @hookscope
  Scenario: Scenario 017-0
    Given precondition 0 is met
    When feature 8 is enabled
    When action 0 is performed
    When undefined step 17-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 017-1
    Given precondition 1 is met
    When feature 9 is enabled
    When action 1 is performed
    When undefined step 17-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 017-2
    Given precondition 2 is met
    When feature 10 is enabled
    When action 2 is performed
    When undefined step 17-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 017-3
    Given precondition 3 is met
    When feature 11 is enabled
    When action 3 is performed
    When undefined step 17-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 017
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

