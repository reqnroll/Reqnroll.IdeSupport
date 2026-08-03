Feature: Corpus feature 008
  Synthetic benchmark feature 008 for performance verification.

  @hookscope
  Scenario: Scenario 008-0
    Given precondition 0 is met
    When feature 32 is enabled
    When action 0 is performed
    When undefined step 8-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 008-1
    Given precondition 1 is met
    When feature 33 is enabled
    When action 1 is performed
    When undefined step 8-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 008-2
    Given precondition 2 is met
    When feature 34 is enabled
    When action 2 is performed
    When undefined step 8-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 008-3
    Given precondition 3 is met
    When feature 35 is enabled
    When action 3 is performed
    When undefined step 8-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 008
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

