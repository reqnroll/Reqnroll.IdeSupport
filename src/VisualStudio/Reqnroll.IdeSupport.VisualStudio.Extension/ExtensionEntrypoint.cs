using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Shell;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.Extension.CommentToggle;
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
        /// <c>LoadedWhen</c> (issue #533, phase 2) declares the extension's load trigger instead
        /// of leaving it to whichever contribution VS happens to activate first — today that is
        /// <c>StepCodeLensProvider</c> when a <c>.cs</c> file opens, which is what makes the eager
        /// server startup in <see cref="OnInitializedAsync"/> fire at a time that varies with the
        /// user's tab layout (see that method's remarks).
        /// <para>
        /// The union of <see cref="SolutionState.NoSolution"/> and <see cref="SolutionState.Exists"/>
        /// is deliberately total. <c>LoadedWhen</c> is a gate, not a hint: a narrower constraint
        /// (e.g. <c>Exists</c> alone) would stop the extension loading in the single-file /
        /// open-folder case, which is precisely the scenario VS's LSP support is designed for. The
        /// goal here is only to make loading <em>eager and deterministic</em>, never to restrict it,
        /// so the constraint is written so it can only ever fire earlier than the status quo.
        /// </para>
        /// </remarks>
        public override ExtensionConfiguration ExtensionConfiguration => new()
        {
            RequiresInProcessHosting = true,
            LoadedWhen = ActivationConstraint.SolutionState(SolutionState.NoSolution)
                         | ActivationConstraint.SolutionState(SolutionState.Exists),
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
            VsShellUtilities.ShutdownToken.Register(() =>
            {
                logger.LogInformation("ExtensionEntrypoint: VsShellUtilities.ShutdownToken fired — disposing LspServerConnectionService.");
                connectionService.Dispose();
            });
            ShutdownToken.Register(() =>
            {
                logger.LogInformation("ExtensionEntrypoint: ExtensionCore.ShutdownToken fired — disposing LspServerConnectionService.");
                connectionService.Dispose();
            });

            return Task.CompletedTask;
        }
    }
}
