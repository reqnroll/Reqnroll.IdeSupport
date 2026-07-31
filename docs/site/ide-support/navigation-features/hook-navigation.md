# Hook Navigation

**Go to Hooks**, invoked from a `.feature` file, lists the hook bindings in
scope at the cursor position, filtered by the tags and `[Scope]`
expressions that apply there:

* From a `Feature:` or `Scenario:` line: shows `[BeforeFeature]`/`[AfterFeature]`
  or `[BeforeScenario]`/`[AfterScenario]` hooks that apply.
* From a step line: additionally shows `[BeforeStep]`/`[AfterStep]` and
  `[BeforeStepBlock]`/`[AfterStepBlock]` hooks.

Selecting an entry navigates to the C# hook method.

TODO(media): 🎬 gif — short jump-to-hook interaction, invoking Go to Hooks
and selecting a result.
