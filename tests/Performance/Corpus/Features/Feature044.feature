Feature: Corpus feature 044
  Synthetic benchmark feature 044 for performance verification.

  @hookscope
  Scenario: Scenario 044-0
    Given precondition 0 is met
    When feature 56 is enabled
    When action 0 is performed
    When undefined step 44-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 044-1
    Given precondition 1 is met
    When feature 57 is enabled
    When action 1 is performed
    When undefined step 44-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 044-2
    Given precondition 2 is met
    When feature 58 is enabled
    When action 2 is performed
    When undefined step 44-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 044-3
    Given precondition 3 is met
    When feature 59 is enabled
    When action 3 is performed
    When undefined step 44-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 044
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

