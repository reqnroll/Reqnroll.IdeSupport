Feature: Corpus feature 011
  Synthetic benchmark feature 011 for performance verification.

  @hookscope
  Scenario: Scenario 011-0
    Given precondition 0 is met
    When feature 44 is enabled
    When action 0 is performed
    When undefined step 11-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 011-1
    Given precondition 1 is met
    When feature 45 is enabled
    When action 1 is performed
    When undefined step 11-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 011-2
    Given precondition 2 is met
    When feature 46 is enabled
    When action 2 is performed
    When undefined step 11-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 011-3
    Given precondition 3 is met
    When feature 47 is enabled
    When action 3 is performed
    When undefined step 11-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 011
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

