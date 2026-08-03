Feature: Corpus feature 031
  Synthetic benchmark feature 031 for performance verification.

  @hookscope
  Scenario: Scenario 031-0
    Given precondition 0 is met
    When feature 4 is enabled
    When action 0 is performed
    When undefined step 31-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 031-1
    Given precondition 1 is met
    When feature 5 is enabled
    When action 1 is performed
    When undefined step 31-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 031-2
    Given precondition 2 is met
    When feature 6 is enabled
    When action 2 is performed
    When undefined step 31-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 031-3
    Given precondition 3 is met
    When feature 7 is enabled
    When action 3 is performed
    When undefined step 31-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 031
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

