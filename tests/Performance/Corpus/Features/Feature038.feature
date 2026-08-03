Feature: Corpus feature 038
  Synthetic benchmark feature 038 for performance verification.

  @hookscope
  Scenario: Scenario 038-0
    Given precondition 0 is met
    When feature 32 is enabled
    When action 0 is performed
    When undefined step 38-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 038-1
    Given precondition 1 is met
    When feature 33 is enabled
    When action 1 is performed
    When undefined step 38-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 038-2
    Given precondition 2 is met
    When feature 34 is enabled
    When action 2 is performed
    When undefined step 38-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 038-3
    Given precondition 3 is met
    When feature 35 is enabled
    When action 3 is performed
    When undefined step 38-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 038
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

