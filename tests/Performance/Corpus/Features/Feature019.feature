Feature: Corpus feature 019
  Synthetic benchmark feature 019 for performance verification.

  @hookscope
  Scenario: Scenario 019-0
    Given precondition 0 is met
    When feature 16 is enabled
    When action 0 is performed
    When undefined step 19-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 019-1
    Given precondition 1 is met
    When feature 17 is enabled
    When action 1 is performed
    When undefined step 19-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 019-2
    Given precondition 2 is met
    When feature 18 is enabled
    When action 2 is performed
    When undefined step 19-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 019-3
    Given precondition 3 is met
    When feature 19 is enabled
    When action 3 is performed
    When undefined step 19-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 019
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

