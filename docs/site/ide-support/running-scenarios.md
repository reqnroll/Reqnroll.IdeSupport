# Running Scenarios

A **Run** lens appears above each `Scenario:`/`Scenario Outline:` line, letting you run (and, where
supported, debug) the generated test for that scenario directly from the `.feature` file — no need
to find the corresponding method in `<Feature>.feature.cs` first.

```{admonition} Requires a build
:class: note

The lens resolves against the generated `<Feature>.feature.cs` test method, which only exists once
the project has built at least once. Nothing appears above a scenario in a project that has never
built successfully — build the project, and the lens appears without needing to reopen the file.
```

```{admonition} Scenario Outline runs every example row together
:class: note

By default (`allowRowTests`, Reqnroll's own setting), all of a Scenario Outline's `Examples:` rows
compile into **one** parameterized test method. Running from the `Scenario Outline:` line runs that
whole method — every row — in one go; there's currently no way to run a single `Examples:` row on
its own from this lens.
```

::::{tab-set}

```{tab-item} Visual Studio
:sync: vs

Above each scenario, a `▶ Run Scenario` (or `▶ Run Scenarios` for an Outline) lens delegates
straight to Visual Studio's own Test Explorer — clicking it opens a Details popup with three
actions:

- **Run** / **Debug** — runs the generated test the same way Test Explorer's own CodeLens would for
  an ordinary `[Fact]`/`[Test]`/`[TestMethod]`, including breakpoints during Debug.
- **Show in Test Explorer** — jumps straight to the test's entry in the Test Explorer tool window,
  where its full pass/fail history and output live.

Once the test has been run at least once (from this lens, Test Explorer, or `dotnet test`), the
lens itself picks up a pass/fail glyph matching VS's own test CodeLens, so you can see a scenario's
last outcome without opening Test Explorer at all.

![](running-scenarios/running-scenarios-vs.gif)
```

```{tab-item} VS Code
:sync: vscode

VS Code has no Reqnroll-owned Run feature for `.feature` files — this is intentionally deferred to
the official **C# Dev Kit** extension, which already discovers Reqnroll's generated test methods
and shows them in its native Test Explorer and editor CodeLens over `<Feature>.feature.cs`.

```{admonition} Install C# Dev Kit to run scenarios
:class: important

Without C# Dev Kit installed, there is currently no way to run or debug a scenario from within VS
Code. Install the [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit)
extension, then use its Test Explorer or its CodeLens on the generated test method in
`<Feature>.feature.cs` to run and debug scenarios.
```

An earlier version of this extension had its own `.feature`-side Run CodeLens with independent
pass/fail tracking. It was removed after live use showed it added nothing C# Dev Kit's own
discovery and Test Explorer integration didn't already cover, and having two separate run
mechanisms was more confusing than helpful.
```

```{tab-item} Rider
:sync: rider

Above each scenario, an inline `▶ Run` lens runs the generated test directly (`dotnet test
--filter`, scoped to that scenario's method). Once it's been run, the lens updates to `✓ Run` or
`✗ Run` depending on the last outcome.

![](running-scenarios/running-scenarios-rider.gif)

```{admonition} Run only — no Debug, no Test Runner tool window presence
:class: warning

This lens only runs the test; there's no debug variant, and the run doesn't show up in Rider's
native Unit Tests tool window — Rider's .NET test integration has no extension point this plugin
can currently plug into for that. Treat it as a quick way to check one scenario without leaving the
`.feature` file; for debugging, or for running/reviewing a whole suite, use Rider's own Test Runner
against the generated test project directly.
```
```

::::

## Troubleshooting

**The Run lens never appears above a scenario.** Confirm the project has built successfully at
least once — the lens needs the generated `<Feature>.feature.cs` test method to resolve against,
which doesn't exist before a first build. If it's still missing after a successful build, check
that the scenario's title actually produces a distinct generated method name (an empty or
whitespace-only `Scenario:` title, for instance, has nothing to resolve to).

**Rider: running a scenario fails with a `dotnet` CLI error.** Rider (and Visual Studio's fallback
path) locate the `dotnet` CLI via `PATH`, then `DOTNET_ROOT`, then well-known install locations. If
none of those resolve — most commonly a GUI-launched IDE process with a minimal environment — the
error message says so explicitly; make sure the .NET SDK is installed and reachable from the
environment your IDE was launched in.

**Visual Studio: the pass/fail glyph never updates, or updates slowly, after a test run.** The
glyph is fed by a Reqnroll-bundled VSTest logger the extension injects into each test run's
runsettings automatically. One environment variable is a kill switch for that pipeline; two more
mirror its outcome stream to disk for troubleshooting or attaching to a bug report — but only one of
those two applies at a time, and which one depends on **how the tests are being run**, not on which
one you'd rather use:

- `REQNROLL_IDE_DISABLE_TEST_LOGGER` — set to any value other than empty, `0`, or `false` to disable
  logger injection entirely. With it disabled, the glyph falls back to Visual Studio's own built-in
  test-outcome cache instead. Use this to rule out (or work around) a misbehaving logger without
  uninstalling the extension.
- **Running through Visual Studio's Test Explorer** (the normal case — this is what feeds the
  glyph): set `REQNROLL_IDE_TEST_LOGGER_MIRROR`, in the environment `devenv.exe` itself was launched
  in, to `1` (writes to an auto-named file under the standard Reqnroll log directory,
  `reqnroll-vs-testlogger-<date>-<pid>.ndjson`) or to an explicit path. The extension injects this
  as a `LogFilePath` runsettings parameter, which is the only delivery mechanism guaranteed to reach
  the test host process regardless of how Visual Studio's test infrastructure ends up spawning it.
- **Running tests without Visual Studio in the picture at all** — a bare `dotnet test`, a CI
  pipeline, or any other direct `vstest` invocation that has the bundled logger wired in manually:
  set `REQNROLL_TESTLOGGER_FILE` (always an explicit path — no `1` shorthand) on that process's own
  environment instead. There's no extension present to inject a runsettings parameter in this case,
  so the logger reads its own environment directly.

These aren't interchangeable — if both happen to be set during a Visual Studio-orchestrated run,
the injected runsettings parameter always wins and `REQNROLL_TESTLOGGER_FILE` is silently ignored,
which is confusing to debug if you don't know that precedence exists. Set whichever one matches how
you're actually running the tests, not both.

`REQNROLL_IDE_DISABLE_TEST_LOGGER` and `REQNROLL_IDE_TEST_LOGGER_MIRROR` are read from devenv's own
process environment on every test run, but a process's environment is fixed at launch — set them as
a user/system environment variable, or in the shell used to start `devenv.exe`, *before* opening
Visual Studio. Setting them while Visual Studio is already running has no effect until it's closed
and reopened from an environment where the variable is set.
