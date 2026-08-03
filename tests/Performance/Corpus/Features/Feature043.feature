Feature: Corpus feature 043
  Synthetic benchmark feature 043 for performance verification.

  @hookscope
  Scenario: Scenario 043-0
    Given precondition 0 is met
    When feature 52 is enabled
    When action 0 is performed
    When undefined step 43-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 043-1
    Given precondition 1 is met
    When feature 53 is enabled
    When action 1 is performed
    When undefined step 43-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 043-2
    Given precondition 2 is met
    When feature 54 is enabled
    When action 2 is performed
    When undefined step 43-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 043-3
    Given precondition 3 is met
    When feature 55 is enabled
    When action 3 is performed
    When undefined step 43-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 043
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

