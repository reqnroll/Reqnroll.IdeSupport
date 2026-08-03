Feature: Corpus feature 047
  Synthetic benchmark feature 047 for performance verification.

  @hookscope
  Scenario: Scenario 047-0
    Given precondition 0 is met
    When feature 8 is enabled
    When action 0 is performed
    When undefined step 47-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 047-1
    Given precondition 1 is met
    When feature 9 is enabled
    When action 1 is performed
    When undefined step 47-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 047-2
    Given precondition 2 is met
    When feature 10 is enabled
    When action 2 is performed
    When undefined step 47-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 047-3
    Given precondition 3 is met
    When feature 11 is enabled
    When action 3 is performed
    When undefined step 47-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 047
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

