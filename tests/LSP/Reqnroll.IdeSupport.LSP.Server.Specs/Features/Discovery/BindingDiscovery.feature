@discovery
Feature: Binding Discovery

Re-homed from the Visual Studio specs.  These run the real ported out-of-process connector
against a prebuilt Reqnroll bindings fixture (latest Reqnroll, net10) and assert on the resulting
ProjectBindingRegistry.

Scenario: Discover bindings from a simple latest Reqnroll project
	Given there is a simple Reqnroll project for the latest version
	And the project is built
	When the binding discovery performed
	Then the discovery succeeds with several step definitions

Scenario: Discover bindings from Reqnroll project with plugin
	Given there is a simple Reqnroll project with plugin for the latest version
	And the project is built
	When the binding discovery performed
	Then the discovery succeeds with several step definitions
	And there is a "Then" step with regex "there should be a step from a plugin"

Scenario: Discover bindings from Reqnroll project with external bindings
	Given there is a simple Reqnroll project with external bindings for the latest version
	And the project is built
	When the binding discovery performed
	Then the discovery succeeds with several step definitions
	And there is a "Then" step with regex "there should be a step from an external assembly"

Scenario: Discover hooks from a Reqnroll project
	Given there is a small Reqnroll project with hooks
	And the project is built
	When the binding discovery performed
	Then the discovery succeeds with hooks

Scenario: Discovered bindings carry their exact method identity
	Given there is a simple Reqnroll project for the latest version
	And the project is built
	When the binding discovery performed
	Then every step definition and hook carries a method identity from the "ReqnrollBindingsFixture" assembly
	And the step "a step on the first same-named class" is bound to "M:ReqnrollBindingsFixture.Identity.First.SharedNameSteps.GivenAStep"
	And the step "a step on the second same-named class" is bound to "M:ReqnrollBindingsFixture.Identity.Second.SharedNameSteps.GivenAStep"
	And the step "these books" is bound to "M:ReqnrollBindingsFixture.Identity.First.SharedNameSteps.GivenTheseBooks(System.Collections.Generic.List{ReqnrollBindingsFixture.Identity.Book})"
