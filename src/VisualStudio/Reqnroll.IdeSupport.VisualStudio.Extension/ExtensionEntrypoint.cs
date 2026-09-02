using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Shell;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.Extension.CommentToggle;
using Reqnroll.IdeSupport.VisualStudio.Extension.Diagnostics;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToMatchingScenarios;
using Reqnroll.IdeSupport.VisualStudio.Extension.HookMatchCountCodeLens;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.Extension.RenameStep;
using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;
#pragma warning disable VSEXTPREVIEW_CODELENS

namespace Reqnroll.IdeSupport.VisualStudio.Extension
{
    /// <summary>
    /// Extension entrypoint for the VisualStudio.Extensibility extension.
    /// </summary>
    [VisualStudioContribution]
    internal class ExtensionEntrypoint : Microsoft.VisualStudio.Extensibility.Extension
    {
        /// <inheritdoc />
        /// <remarks>
        /// Deliberately no <c>LoadedWhen</c> (issue #533). It was tried — a total
        /// <c>SolutionState.NoSolution | SolutionState.Exists</c> union, meant to make extension
        /// load deterministic rather than depending on whichever contribution VS activates first —
        /// and measurement rejected it on both counts:
        /// <list type="bullet">
        /// <item>
        /// It fixed nothing. Without it, VS activates the <c>LanguageServerProvider</c> ~1.1–1.4s
        /// after extension load for a solution whose only restored tab is a <c>.feature</c> file
        /// (three runs, 2026-08-30). The delayed activation the issue was filed about did not
        /// reproduce.
        /// </item>
        /// <item>
        /// It looked actively harmful on the first launch after a deploy: both deploys carrying it
        /// produced a bad first run (once 13.4s to activate, once no activation at all within the
        /// ~20s the session lasted), while the build without it was fine on its own
        /// first-after-deploy run. <c>LoadedWhen</c> is a gate VS must evaluate while it is
        /// rebuilding the extension cache; with no gate there is nothing to get wrong.
        /// </item>
        /// </list>
        /// Do not reintroduce it without a measured problem it solves.
        /// </remarks>
        public override ExtensionConfiguration ExtensionConfiguration => new()
        {
            RequiresInProcessHosting = true,
        };

        /// <inheritdoc />
        protected override void InitializeServices(IServiceCollection serviceCollection)
        {
            base.InitializeServices(serviceCollection);

            // Single, shared logging sink for the whole extension (issue #84): previously ~20
            // classes each `new`'d their own SynchronousFileLogger (mostly defaulting to
            // TraceLevel.Warning, silently dropping LogInfo) while also taking a DI-injected
            // TraceSource that nothing ever attached a listener to. One IdeSupportCompositeLogger,
            // registered once and consumed everywhere via ILogger<T>, replaces both.
            var logger = new IdeSupportCompositeLogger()
                .Add(new IdeSupportDebugLogger())
                .Add(new SynchronousFileLogger("vs", "ext", TraceLevel.Info));
            serviceCollection.AddSingleton<IIdeSupportLogger>(logger);
            serviceCollection.AddSingleton<ILoggerFactory>(sp =>
                new IdeSupportLoggerFactory(sp.GetRequiredService<IIdeSupportLogger>()));
            serviceCollection.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

            // Shared holder for the runtime-created "Find Step Usages" components.  Registering
            // it here makes it resolvable by constructor injection in both ReqnrollLanguageClient
            // (which populates it) and FindStepUsagesCommand / future command filters (which read it),
            // rather than relying on the undocumented ability to inject one contribution class into
            // another.
            serviceCollection.AddSingleton<FindStepUsagesState>();
            serviceCollection.AddSingleton<FindUnusedStepDefinitionsState>();
            serviceCollection.AddSingleton<GoToHooksState>();
            serviceCollection.AddSingleton<GoToMatchingScenariosState>();
            serviceCollection.AddSingleton<StepCodeLensState>();
            serviceCollection.AddSingleton<CommentToggleState>();
            serviceCollection.AddSingleton<RenameStepState>();
            // ExtensionPart subclasses are not auto-registered by the framework; must be explicit.
            serviceCollection.AddSingleton<StepCodeLensProvider>();
            serviceCollection.AddSingleton<HookMatchCountCodeLensProvider>();

            // Owns the LSP server process + duplex pipe. Registered as a singleton; resolved
            // eagerly from OnInitializedAsync below (NOT via ReqnrollLanguageClient's own
            // constructor — see the remarks there for why that turned out not to be early enough).
            serviceCollection.AddSingleton<LspServerConnectionService>();
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// This is the extension's true "load" hook: <c>ExtensionCore.CreateAsync</c> fires it
        /// exactly once, the first time VS requests <b>any</b> service this extension contributes —
        /// not specifically <c>ReqnrollLanguageClient</c>. Confirmed by decompiling
        /// <c>Microsoft.VisualStudio.Extensibility.Framework.dll</c>: <c>CreateAsync</c> guards the
        /// call with <c>bool triggerOnInitialized = serviceProvider == null;</c>, so whichever
        /// contribution VS activates first — in practice <see cref="StepCodeLensProvider"/>, which
        /// activates as soon as a <c>.cs</c> file is opened — is what starts the clock, not the
        /// <c>.feature</c>-file-gated <c>LanguageServerProvider</c>.
        /// </para>
        /// <para>
        /// Resolving <see cref="LspServerConnectionService"/> here (instead of via
        /// <c>ReqnrollLanguageClient</c>'s constructor) is what actually front-loads server startup:
        /// three logged VS sessions showed <c>StepCodeLensProvider</c> activating 8–18 seconds before
        /// <c>ReqnrollLanguageClient</c> did in a "open a .cs file first" workflow, versus the ~20–40ms
        /// gap the constructor-injection approach was actually delivering (see project memory
        /// "project-eager-lsp-startup-service" for the log analysis that found this).
        /// </para>
        /// </remarks>
        protected override Task OnInitializedAsync(VisualStudioExtensibility extensibility, CancellationToken cancellationToken)
        {
            var logger = ServiceProvider.GetRequiredService<ILogger<ExtensionEntrypoint>>();
            logger.LogInformation(
                "ExtensionEntrypoint: OnInitializedAsync — resolving LspServerConnectionService eagerly.");

            // Resolving (not just registering) is what triggers construction of the singleton,
            // whose own constructor kicks off server process launch — see LspServerConnectionService.
            var connectionService = ServiceProvider.GetRequiredService<LspServerConnectionService>();

            // None of VS.Extensibility's own disposal paths reaches this singleton: the generated
            // ILanguageServerProvider wrapper (LanguageServerProviderService, decompiled from
            // Microsoft.VisualStudio.Extensibility.dll) has an empty Dispose() body that never
            // forwards to ReqnrollLanguageClient; ExtensionCore.Dispose(bool) never disposes the DI
            // container this singleton lives in; and even ExtensionCore.ShutdownToken (cancelled
            // inside that same Dispose(bool)) was confirmed by logging to never fire on a normal
            // window-close of an in-proc (RequiresInProcessHosting) extension — devenv.exe appears
            // to tear down without ever calling ExtensionCore.Dispose() at all in that case.
            //
            // Microsoft.VisualStudio.Shell.VsShellUtilities.ShutdownToken is the classic, static,
            // shell-level signal instead: driven directly by the shell's own shutdown broadcast
            // rather than any per-object Dispose() chain, and documented as firing *earlier* than
            // package-level disposal tokens (see AsyncPackage.DisposalToken remarks). Registering
            // on both costs nothing — LspServerConnectionService.Dispose() is idempotent — but this
            // one is the signal actually expected to fire.
            // Issue #555: that "expected to fire" is exactly what is now in doubt. The extension
            // log for a session where the user switched solutions ends at this callback, with no
            // LSP service afterwards for the rest of the session — and disposing this singleton is
            // one-way, since OnInitializedAsync runs once per extension instance and nothing
            // re-resolves the service after it. Whether that log ends because the IDE exited (the
            // token behaving as documented) or because a solution close fired it spuriously and
            // neutered a live session decides where the fix goes, and the logs as they stand
            // cannot tell those apart. LspLifecycleForensics adds the evidence that can.
            var forensics = new LspLifecycleForensics(ServiceProvider.GetRequiredService<IIdeSupportLogger>());
            forensics.LogProcessIdentity(typeof(ExtensionEntrypoint).Assembly.Location);

            VsShellUtilities.ShutdownToken.Register(() =>
            {
                logger.LogInformation("ExtensionEntrypoint: VsShellUtilities.ShutdownToken fired — disposing LspServerConnectionService.");
                forensics.OnShutdownTokenFired("VsShellUtilities.ShutdownToken");
                connectionService.Dispose();
            });
            ShutdownToken.Register(() =>
            {
                logger.LogInformation("ExtensionEntrypoint: ExtensionCore.ShutdownToken fired — disposing LspServerConnectionService.");
                forensics.OnShutdownTokenFired("ExtensionCore.ShutdownToken");
                connectionService.Dispose();
            });

            return Task.CompletedTask;
        }
    }
}
