Feature: Linked file and excluded file ownership

Membership is conferred only by the reqnroll/projectFiles index, never inferred from where a
file sits on disk. These scenarios drive the routing invariants the Feature Designs doc's
"Linked Files and Project Membership" section defines, from the client side:

  * a file's absence from the index means "pending" until that project's first baseline
    arrives, and "deliberately excluded" thereafter;
  * opening a file confers neither membership nor binding-dependent behaviour;
  * a file re-included by a delta gets its ownership, and its features, back.

The workspace folder is the solution root and each project lives in its own sub-folder, so a
path under one project's folder is genuinely outside the other's.

Background:
	Given the LSP server is started

# ── Pending, then excluded ───────────────────────────────────────────────────────
#
# Before a project's first baseline the server cannot distinguish "not reported yet" from
# "excluded", so ResolveOwners falls back to folder containment and binding-dependent features
# work. Once the baseline lands, the same absence becomes a deliberate exclusion.

Scenario: Before any baseline a file in the project folder is treated as owned
	When the project "Home.csproj" is announced in folder "Home"
	And the C# step definition file "Home/Steps.cs" is opened with
		"""
		using Reqnroll;
		namespace Home
		{
			[Binding]
			public class Steps
			{
				[When("I press add")]
				public void WhenIPressAdd() { }
			}
		}
		"""
	And the feature file "Home/Pending.feature" is opened with
		"""
		Feature: Pending

		Scenario: S
			When I press add
		"""
	Then the feature step "I press add" is reported as bound

Scenario: A baseline that omits a file in the project folder excludes it
	When the project "Home.csproj" is announced in folder "Home"
	And the project files baseline is announced for "Home.csproj" with
		| path                 | role    |
		| Home/Included.feature | Feature |
		| Home/Steps.cs         | Binding |
	And the C# step definition file "Home/Steps.cs" is opened with
		"""
		using Reqnroll;
		namespace Home
		{
			[Binding]
			public class Steps
			{
				[When("I press add")]
				public void WhenIPressAdd() { }
			}
		}
		"""
	And the feature file "Home/Excluded.feature" is opened with
		"""
		Feature: Excluded

		Scenario: S
			When I press add
		"""
	Then the semantic tokens include a "reqnroll.keyword" token for "Scenario:"
	And no "reqnroll.binding" diagnostic is published for "Home/Excluded.feature"

# ── Opening confers nothing ──────────────────────────────────────────────────────

Scenario: An excluded C# file does not inject bindings into the project that contains it
	When the project "Home.csproj" is announced in folder "Home"
	And the project files baseline is announced for "Home.csproj" with
		| path                  | role    |
		| Home/Included.feature | Feature |
		| Home/Steps.cs         | Binding |
	And the C# step definition file "Home/Steps.cs" is opened with
		"""
		using Reqnroll;
		namespace Home
		{
			[Binding]
			public class Steps
			{
				[When("I press add")]
				public void WhenIPressAdd() { }
			}
		}
		"""
	And the C# step definition file "Home/Rogue.cs" is opened with
		"""
		using Reqnroll;
		namespace Home
		{
			[Binding]
			public class Rogue
			{
				[When("I press rogue")]
				public void WhenIPressRogue() { }
			}
		}
		"""
	And the feature file "Home/Included.feature" is opened with
		"""
		Feature: Included

		Scenario: S
			When I press rogue
		"""
	Then the feature step "I press rogue" is reported as unbound

# ── Re-inclusion ─────────────────────────────────────────────────────────────────

Scenario: A delta that re-adds an excluded binding file restores its bindings
	When the project "Home.csproj" is announced in folder "Home"
	And the project files baseline is announced for "Home.csproj" with
		| path                  | role    |
		| Home/Included.feature | Feature |
		| Home/Steps.cs         | Binding |
	And the C# step definition file "Home/Steps.cs" is opened with
		"""
		using Reqnroll;
		namespace Home
		{
			[Binding]
			public class Steps
			{
				[When("I press add")]
				public void WhenIPressAdd() { }
			}
		}
		"""
	And the C# step definition file "Home/Rogue.cs" is opened with
		"""
		using Reqnroll;
		namespace Home
		{
			[Binding]
			public class Rogue
			{
				[When("I press rogue")]
				public void WhenIPressRogue() { }
			}
		}
		"""
	And the feature file "Home/Included.feature" is opened with
		"""
		Feature: Included

		Scenario: S
			When I press rogue
		"""
	Then the feature step "I press rogue" is reported as unbound
	When the project files delta adds files for "Home.csproj" with
		| path          | role    |
		| Home/Rogue.cs | Binding |
	And the C# step definition file "Home/Rogue.cs" is changed to
		"""
		using Reqnroll;
		namespace Home
		{
			[Binding]
			public class Rogue
			{
				[When("I press rogue")]
				public void WhenIPressRogue() { }
			}
		}
		"""
	Then the feature step "I press rogue" is reported as bound

# ── A file linked into two projects ──────────────────────────────────────────────
#
# Home/Shared.feature is physically under Home but is claimed by both baselines — the linked
# file case. Only Linking declares a binding for the step. Per the design, "a step is unmatched
# only if unmatched in all owners", so the step should read as bound.
#
# KNOWN GAP (issue #558) — ignored, not deleted. This scenario fails today: an OPEN feature
# file's match set is built against ResolvePrimaryOwner alone
# (GherkinDocumentTaggerService.ScanAsync), so only
# the primary owner's registry is consulted. Home wins primary-owner resolution because the file
# sits inside its folder, and Home has no binding for the step, so it reads as undefined even
# though Linking binds it. The closed-file path (RescanClosedFile) already iterates every owner,
# so the two paths disagree. Un-ignore this scenario with the fix for #558.

@ignore
Scenario: A step in a linked feature file is bound when any owning project binds it
	When the project "Home.csproj" is announced in folder "Home"
	And the project "Linking.csproj" is announced in folder "Linking"
	And the project files baseline is announced for "Home.csproj" with
		| path                | role    |
		| Home/Shared.feature | Feature |
		| Home/HomeSteps.cs   | Binding |
	And the C# step definition file "Home/HomeSteps.cs" is opened with
		"""
		using Reqnroll;
		namespace Home
		{
			[Binding]
			public class HomeSteps
			{
				[When("I press home")]
				public void WhenIPressHome() { }
			}
		}
		"""
	And the project files baseline is announced for "Linking.csproj" with
		| path                    | role    |
		| Home/Shared.feature     | Feature |
		| Linking/LinkingSteps.cs | Binding |
	And the C# step definition file "Linking/LinkingSteps.cs" is opened with
		"""
		using Reqnroll;
		namespace Linking
		{
			[Binding]
			public class LinkingSteps
			{
				[When("I press linked")]
				public void WhenIPressLinked() { }
			}
		}
		"""
	And the feature file "Home/Shared.feature" is opened with
		"""
		Feature: Shared

		Scenario: S
			When I press linked
		"""
	Then the feature step "I press linked" is reported as bound
	And no "reqnroll.binding" diagnostic is published for "Home/Shared.feature"
