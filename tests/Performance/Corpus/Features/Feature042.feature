Feature: Corpus feature 042
  Synthetic benchmark feature 042 for performance verification.

  @hookscope
  Scenario: Scenario 042-0
    Given precondition 0 is met
    When feature 48 is enabled
    When action 0 is performed
    When undefined step 42-0 occurs
    Then the result is 0
    Then the result is computed cleanly

  Scenario: Scenario 042-1
    Given precondition 1 is met
    When feature 49 is enabled
    When action 1 is performed
    When undefined step 42-1 occurs
    Then the result is 1
    Then the result is computed cleanly

  Scenario: Scenario 042-2
    Given precondition 2 is met
    When feature 50 is enabled
    When action 2 is performed
    When undefined step 42-2 occurs
    Then the result is 2
    Then the result is computed cleanly

  Scenario: Scenario 042-3
    Given precondition 3 is met
    When feature 51 is enabled
    When action 3 is performed
    When undefined step 42-3 occurs
    Then the result is 3
    Then the result is computed cleanly

  Scenario Outline: Outline 042
    Given precondition <n> is met
    When action <n> is performed
    Then the result is <outcome>

    Examples:
      | n | outcome |
      | 1 | success |
      | 2 | failure |

