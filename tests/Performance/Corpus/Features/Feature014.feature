Feature: Corpus feature 014
  Synthetic benchmark feature 014 for performance verification.

  @hookscope
  Scenario: Scenario 014-0
    Given precondition 0 is met
    When feature 56 is enabled
    When action 0 is performed
    When undefined step 14-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 014-1
    Given precondition 1 is met
    When feature 57 is enabled
    When action 1 is performed
    When undefined step 14-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 014-2
    Given precondition 2 is met
    When feature 58 is enabled
    When action 2 is performed
    When undefined step 14-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 014-3
    Given precondition 3 is met
    When feature 59 is enabled
    When action 3 is performed
    When undefined step 14-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 014
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

