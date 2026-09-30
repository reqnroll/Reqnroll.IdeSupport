@discovery
Feature: Connector and Roslyn discovery sequencing

The server has two sources of bindings: the out-of-process connector, which reflects over a
built assembly, and the Roslyn live path, which parses an open .cs on every edit. The
Architecture doc states the merge rule -- Roslyn-derived bindings for a file replace previous
entries for that file, while connector output replaces the entire registry -- but until now no
test ran both against one server, so the rule was only ever exercised one source at a time.

This is the seam the duplicate-binding family of issues kept reappearing in (#469, #503, #515,
#517, #554): bindings doubling, or being lost, when discovery re-ran alongside an edit.

A doubled binding is observable without counting anything: two registry entries matching the
same step text make that step ambiguous, which surfaces as an Error diagnostic from
"reqnroll.binding". So "no binding diagnostic" asserts both that the step still matched and
that it matched exactly once.

Scenario: Connector-discovered bindings match steps with no .cs file open
	Given the LSP server is started
	When the project is announced with the prebuilt bindings fixture
	And the feature file "Calc.feature" is opened with
		"""
		Feature: Calc

		Scenario: S
			When I press add
			When I press a button the fixture never defines
		"""
	Then the feature step "I press add" is reported as bound within 30 seconds
	And the feature step "I press a button the fixture never defines" is reported as unbound

Scenario: A Roslyn edit adds a binding without disturbing connector-discovered ones
	Given the LSP server is started
	When the project is announced with the prebuilt bindings fixture
	And the C# step definition file "Extra.cs" is opened with
		"""
		using Reqnroll;
		namespace Sample
		{
			[Binding]
			public class Extra
			{
				[When("I press subtract")]
				public void WhenIPressSubtract() { }
			}
		}
		"""
	And the feature file "Mixed.feature" is opened with
		"""
		Feature: Mixed

		Scenario: S
			When I press add
			When I press subtract
			When I press a button nobody defines
		"""
	Then the feature step "I press add" is reported as bound within 30 seconds
	And the feature step "I press subtract" is reported as bound
	And the feature step "I press a button nobody defines" is reported as unbound

Scenario: Repeated edits to one .cs replace its bindings rather than accumulating them
	Given the LSP server is started
	When the project is announced with the prebuilt bindings fixture
	And the feature file "Repeat.feature" is opened with
		"""
		Feature: Repeat

		Scenario: S
			When I press add
			When I press subtract
		"""
	# Wait for connector discovery to land before touching the .cs, so the assertions below are
	# about the merge rule and not about which source happened to finish first. The fixture
	# defines "I press add" but not "I press subtract", so "I press subtract" being reported as
	# unbound is a positive signal that the connector's registry has actually landed — with an
	# empty registry no step is classified undefined, so the old "reported as bound within N
	# seconds" step passed vacuously before the connector had run (issue #817). Budgeted at 30s,
	# like the other scenarios' first connector-landing check above: this is the same
	# out-of-process connector cold start, and this scenario runs alone when reproducing #817.
	Then the feature step "I press subtract" is reported as unbound within 30 seconds
	And the feature step "I press add" is reported as bound
	When the C# step definition file "Extra.cs" is opened with
		"""
		using Reqnroll;
		namespace Sample
		{
			[Binding]
			public class Extra
			{
				[When("I press subtract")]
				public void WhenIPressSubtract() { }
			}
		}
		"""
	Then the feature step "I press subtract" is reported as bound
	And the feature step "I press add" is reported as bound
	When the C# step definition file "Extra.cs" is changed to
		"""
		using Reqnroll;
		namespace Sample
		{
			[Binding]
			public class Extra
			{
				[When("I press subtract")]
				public void WhenIPressSubtract() { }

				[When("I press divide")]
				public void WhenIPressDivide() { }
			}
		}
		"""
	And the C# step definition file "Extra.cs" is changed to
		"""
		using Reqnroll;
		namespace Sample
		{
			[Binding]
			public class Extra
			{
				[When("I press subtract")]
				public void WhenIPressSubtract() { }

				[When("I press divide")]
				public void WhenIPressDivide() { }

				[When("I press modulo")]
				public void WhenIPressModulo() { }
			}
		}
		"""
	Then the feature step "I press subtract" is reported as bound
	And the feature step "I press add" is reported as bound
	And no "reqnroll.binding" diagnostic is published for "Repeat.feature"
