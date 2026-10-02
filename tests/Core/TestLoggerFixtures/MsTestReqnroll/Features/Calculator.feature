Feature: Calculator

Scenario: Adding two numbers
    Given the first number is 50
    And the second number is 70
    When the two numbers are added
    Then the result should be 120

Scenario: A step in the middle fails
    Given the first number is 1
    When the calculation explodes
    Then the result should be 2

Scenario Outline: Adding rows
    Given the first number is <first>
    And the second number is <second>
    When the two numbers are added
    Then the result should be <result>

Examples:
    | first | second | result |
    | 1     | 2      | 3      |
    | 10    | 20     | 30     |
    | 5     | 5      | 11     |
