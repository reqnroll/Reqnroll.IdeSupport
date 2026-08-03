Feature: Corpus feature 046
  Synthetic benchmark feature 046 for performance verification.

  @hookscope
  Scenario: Scenario 046-0
    Given precondition 0 is met
    When feature 4 is enabled
    When action 0 is performed
    When undefined step 46-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 046-1
    Given precondition 1 is met
    When feature 5 is enabled
    When action 1 is performed
    When undefined step 46-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 046-2
    Given precondition 2 is met
    When feature 6 is enabled
    When action 2 is performed
    When undefined step 46-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 046-3
    Given precondition 3 is met
    When feature 7 is enabled
    When action 3 is performed
    When undefined step 46-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 046
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

