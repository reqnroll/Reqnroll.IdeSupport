#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;
using Reqnroll.IdeSupport.VisualStudio.TestLogger;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.TestOutcomes;

/// <summary>
/// Sends the custom <c>reqnroll/testOutcomes/registerRun</c> and <c>reqnroll/testOutcomes/getOutcome</c>
/// requests to the LSP server (LSP-server outcome pipeline refactor) — the VS-side counterpart to
/// <c>RegisterTestRunHandler</c>/<c>GetTestOutcomeHandler</c>. Replaces the VS-only, in-proc
/// <c>TestOutcomeListener</c>/<c>TestOutcomeStore</c> pair: the receiver, store, and persistence now
/// live once in the server, shared by every IDE, instead of VS growing its own.
/// </summary>
internal sealed class RunTestOutcomeService
{
    private readonly LspInterceptingPipe _pipe;
    private readonly ILogger<RunTestOutcomeService> _logger;

    public RunTestOutcomeService(LspInterceptingPipe pipe, ILogger<RunTestOutcomeService> logger)
    {
        _pipe = pipe;
        _logger = logger;
    }

    /// <summary>
    /// Asks the server to mint a fresh, single-use endpoint+token for one test run. Returns null on
    /// any failure (server not reachable, malformed response) — the caller then injects nothing for
    /// this run, exactly like the old in-proc listener's "couldn't start" path.
    /// </summary>
    public async Task<TestRunRegistration?> RegisterRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _pipe
                .SendRequestToServerAsync(ReqnrollMethodNames.RegisterTestRun, "{}", cancellationToken)
                .ConfigureAwait(false);

            return MapRegistration(result as JObject);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RunTestOutcomeService: RegisterRunAsync failed.");
            return null;
        }
    }

    /// <summary>
    /// Queries the server for a generated test method's last-known outcome. Returns null when no run
    /// has reported it this session (or the aggregate call failed), so the caller falls back to the
    /// reflection bridge exactly as before.
    /// </summary>
    public async Task<RunTestOutcomeEntry?> GetOutcomeAsync(string assemblyPath, string typeFullName, string methodName, CancellationToken cancellationToken)
    {
        try
        {
            var paramsJson = new LspParamsBuilder()
                .AddString("assemblyPath", assemblyPath)
                .AddString("typeFullName", typeFullName)
                .AddString("methodName", methodName)
                .Build();

            var result = await _pipe
                .SendRequestToServerAsync(ReqnrollMethodNames.GetTestOutcome, paramsJson, cancellationToken)
                .ConfigureAwait(false);

            return MapOutcome(result as JObject);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RunTestOutcomeService: GetOutcomeAsync failed for {TypeFullName}.{MethodName}.", typeFullName, methodName);
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal static TestRunRegistration? MapRegistration(JObject? result)
    {
        if (result is null || !(result["success"]?.Value<bool>() ?? false))
            return null;

        var runId = result["runId"]?.Value<string>();
        var endpoint = result["endpoint"]?.Value<string>();
        var token = result["token"]?.Value<string>();
        if (string.IsNullOrEmpty(runId) || string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(token))
            return null;

        return new TestRunRegistration(runId!, endpoint!, token!);
    }

    internal static RunTestOutcomeEntry? MapOutcome(JObject? result)
    {
        if (result is null || !(result["found"]?.Value<bool>() ?? false))
            return null;

        var rows = (result["rows"] as JArray ?? new JArray())
            .OfType<JObject>()
            .Select(r => new RunTestOutcomeRow(
                r["displayName"]?.Value<string>() ?? string.Empty,
                r["outcome"]?.Value<string>() ?? "None",
                r["durationMs"]?.Value<double>() ?? 0,
                r["errorMessage"]?.Value<string>(),
                r["stepCount"]?.Value<int>() ?? 0,
                r["failedStepIndex"]?.Value<int?>(),
                r["failedStepText"]?.Value<string>(),
                r["failedStepOutcome"]?.Value<string>()))
            .ToList();

        return new RunTestOutcomeEntry(
            result["aggregate"]?.Value<string>() ?? "None",
            rows,
            result["lastUpdatedUtc"]?.Value<DateTime>() ?? DateTime.MinValue,
            result["isRunning"]?.Value<bool>() ?? false,
            result["isStale"]?.Value<bool>() ?? false);
    }
}
