Feature: Corpus feature 021
  Synthetic benchmark feature 021 for performance verification.

  @hookscope
  Scenario: Scenario 021-0
    Given precondition 0 is met
    When feature 24 is enabled
    When action 0 is performed
    When undefined step 21-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 021-1
    Given precondition 1 is met
    When feature 25 is enabled
    When action 1 is performed
    When undefined step 21-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 021-2
    Given precondition 2 is met
    When feature 26 is enabled
    When action 2 is performed
    When undefined step 21-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 021-3
    Given precondition 3 is met
    When feature 27 is enabled
    When action 3 is performed
    When undefined step 21-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 021
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

