Feature: Corpus feature 015
  Synthetic benchmark feature 015 for performance verification.

  @hookscope
  Scenario: Scenario 015-0
    Given precondition 0 is met
    When feature 0 is enabled
    When action 0 is performed
    When undefined step 15-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 015-1
    Given precondition 1 is met
    When feature 1 is enabled
    When action 1 is performed
    When undefined step 15-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 015-2
    Given precondition 2 is met
    When feature 2 is enabled
    When action 2 is performed
    When undefined step 15-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 015-3
    Given precondition 3 is met
    When feature 3 is enabled
    When action 3 is performed
    When undefined step 15-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 015
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

