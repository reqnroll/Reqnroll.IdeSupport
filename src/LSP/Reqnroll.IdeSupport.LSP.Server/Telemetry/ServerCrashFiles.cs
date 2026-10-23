using System.Text.Json;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Telemetry;

/// <summary>
/// What a server process records about its own fatal exception, already reduced to telemetry-safe values
/// (scrubbed messages, product-only frames) so the file on disk holds nothing that telemetry would not.
/// A dying server cannot send telemetry (its events go through the client, over a pipe that is closing), so the
/// next server to start sends it instead; see <see cref="ServerCrashFiles"/>.
/// </summary>
public sealed class ServerCrashRecord
{
    /// <summary>Bumped when the shape changes; a reader skips records with a version it does not know.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The schema version this record was written with.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>When the exception was raised, in UTC.</summary>
    public DateTime CrashedAtUtc { get; set; }

    /// <summary>Milliseconds from the crashed process starting to the exception.</summary>
    public long? UptimeMs { get; set; }

    /// <summary>The crashed run's <c>SessionId</c>.</summary>
    public string? SessionId { get; set; }

    /// <summary>The crashed run's <c>ClientSessionId</c>.</summary>
    public string? ClientSessionId { get; set; }

    /// <summary>The crashed run's <c>IdeClient</c>.</summary>
    public string? IdeClient { get; set; }

    /// <summary>The crashed run's <c>ServerVersion</c>.</summary>
    public string? ServerVersion { get; set; }

    /// <summary>Full type name of the outermost exception.</summary>
    public string? ExceptionType { get; set; }

    /// <summary>The outermost exception's scrubbed message.</summary>
    public string? Message { get; set; }

    /// <summary>The outermost exception's product frames.</summary>
    public string? StackFrames { get; set; }

    /// <summary>Simple name of the class holding the outermost exception's topmost product frame.</summary>
    public string? Source { get; set; }

    /// <summary>How many exceptions are nested inside the outermost one.</summary>
    public int InnerExceptionCount { get; set; }

    /// <summary>Full type name of the innermost exception, when there is one.</summary>
    public string? InnerExceptionType { get; set; }

    /// <summary>The innermost exception's scrubbed message.</summary>
    public string? InnerMessage { get; set; }

    /// <summary>The innermost exception's product frames.</summary>
    public string? InnerStackFrames { get; set; }

    /// <summary>Builds the record for <paramref name="exception"/>. Never throws for a non-null exception.</summary>
    public static ServerCrashRecord From(
        Exception exception, DateTime crashedAtUtc, long? uptimeMs,
        string? sessionId, string? clientSessionId, string? ideClient, string? serverVersion)
    {
        var record = new ServerCrashRecord
        {
            CrashedAtUtc = crashedAtUtc,
            UptimeMs = uptimeMs,
            SessionId = sessionId,
            ClientSessionId = clientSessionId,
            IdeClient = ideClient,
            ServerVersion = serverVersion,
            ExceptionType = exception.GetType().FullName,
            Message = TelemetryScrubber.ScrubMessage(exception.Message),
        };
        var captured = ExceptionStackSanitizer.Capture(exception);
        record.Source = captured?.Source;
        record.StackFrames = captured?.Sanitize();

        var innermost = exception;
        var depth = 0;
        // Bounded: a cyclic chain is impossible for real exceptions, but this runs while the process is dying.
        while (innermost.InnerException is { } next && depth < 64)
        {
            innermost = next;
            depth++;
        }
        if (depth > 0)
        {
            record.InnerExceptionCount = depth;
            record.InnerExceptionType = innermost.GetType().FullName;
            record.InnerMessage = TelemetryScrubber.ScrubMessage(innermost.Message);
            record.InnerStackFrames = ExceptionStackSanitizer.Sanitize(innermost);
        }
        return record;
    }

    /// <summary>
    /// The <c>Error.UnhandledException</c> properties for this record. The crashed run's identity keys are set
    /// explicitly, so the identity decorator keeps them instead of stamping the reporting server's own.
    /// </summary>
    public Dictionary<string, object?> ToEventProperties(DateTime nowUtc)
    {
        var properties = new Dictionary<string, object?>
        {
            [CrashReport.CrashKindKey] = CrashKind.UnhandledException,
            ["IsFatal"] = true,
            [ReportedAfterRestartKey] = true,
        };
        var age = nowUtc - CrashedAtUtc;
        if (age >= TimeSpan.Zero)
            properties[CrashAgeMsKey] = (long)age.TotalMilliseconds;
        Add(ServerLifecycleTelemetry.UptimeMsKey, UptimeMs);
        Add(IdentityStampingLspTelemetryService.SessionIdKey, SessionId);
        Add(IdentityStampingLspTelemetryService.ClientSessionIdKey, ClientSessionId);
        Add(IdentityStampingLspTelemetryService.IdeClientKey, IdeClient);
        Add(IdentityStampingLspTelemetryService.ServerVersionKey, ServerVersion);
        Add(CrashReport.ExceptionTypeKey, ExceptionType);
        Add(CrashReport.MessageKey, Message);
        Add(CrashReport.StackFramesKey, StackFrames);
        Add(TelemetryProperties.Source, Source);
        if (InnerExceptionCount > 0)
            properties[CrashReport.InnerExceptionCountKey] = InnerExceptionCount;
        Add(CrashReport.InnerExceptionTypeKey, InnerExceptionType);
        Add(CrashReport.InnerMessageKey, InnerMessage);
        Add(CrashReport.InnerStackFramesKey, InnerStackFrames);
        return properties;

        void Add(string key, object? value)
        {
            if (value is not null && value is not "")
                properties[key] = value;
        }
    }

    /// <summary>Property key: <see langword="true"/> on an <c>Error.UnhandledException</c> sent by a later server for a run that crashed.</summary>
    public const string ReportedAfterRestartKey = "ReportedAfterRestart";

    /// <summary>Property key: milliseconds between the crash and the report.</summary>
    public const string CrashAgeMsKey = "CrashAgeMs";
}

/// <summary>
/// Crash files: a server that is dying from an unhandled exception writes a <see cref="ServerCrashRecord"/> to
/// <see cref="Directory"/>, and the next server to start, whichever IDE or project started it, sends each
/// pending record as an <c>Error.UnhandledException</c> (<c>IsFatal = true</c>) and deletes it. Records older than
/// <see cref="MaxAge"/> are deleted unsent.
/// </summary>
/// <remarks>
/// Only managed exceptions reach the handler: a stack overflow, <c>Environment.FailFast</c>, a native crash or a
/// killed process write nothing, which is why clients also report the exit itself. Two servers starting at once
/// cannot both send a record: each claims it by renaming it first, and only one rename succeeds.
/// </remarks>
public sealed class ServerCrashFiles
{
    /// <summary>Records older than this are deleted without being reported.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(72);

    /// <summary>The most records one server start reports; the rest wait for the next start.</summary>
    public const int MaxReportsPerStart = 10;

    // How old a temp or claimed file must be before a later start treats it as abandoned.
    private static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(10);

    private const string FilePrefix = "reqnroll-crash-";
    private const string RecordExtension = ".json";
    private const string ClaimedMarker = ".reporting-";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Initializes a new instance over <paramref name="directory"/>.</summary>
    public ServerCrashFiles(string directory)
    {
        Directory = directory;
    }

    /// <summary>The folder for crash files: <c>crash-reports</c> under the Reqnroll log directory.</summary>
    public static string DefaultDirectory() => Path.Combine(ReqnrollLogPaths.ResolveLogDirectory(), "crash-reports");

    /// <summary>The folder this instance reads and writes.</summary>
    public string Directory { get; }

    /// <summary>Writes <paramref name="record"/> atomically (temp file, then rename). Never throws.</summary>
    public bool TryWrite(ServerCrashRecord record)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var name = FilePrefix + (ServerSession.Normalize(record.SessionId) ?? ServerSession.NewId());
            var target = Path.Combine(Directory, name + RecordExtension);
            var temp = Path.Combine(Directory, name + ".tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(record, JsonOptions));
            File.Move(temp, target, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Claims, sends through <paramref name="send"/> and deletes each pending record; records older than
    /// <see cref="MaxAge"/>, and unreadable ones, are deleted unsent. Returns how many were sent. Never throws.
    /// </summary>
    public int ReportPending(Action<ServerCrashRecord> send, DateTime nowUtc)
    {
        string[] files;
        try
        {
            if (!System.IO.Directory.Exists(Directory))
                return 0;
            files = System.IO.Directory.GetFiles(Directory, FilePrefix + "*");
        }
        catch (Exception)
        {
            return 0;
        }

        var sent = 0;
        foreach (var file in files)
        {
            try
            {
                var age = nowUtc - File.GetLastWriteTimeUtc(file);
                if (!file.EndsWith(RecordExtension, StringComparison.OrdinalIgnoreCase))
                {
                    // A temp file from a crash mid-write, or a claim whose reporter died: removed once it is old
                    // enough that no live process can still own it.
                    if (age > LeftoverAge)
                        TryDelete(file);
                    continue;
                }
                if (age > MaxAge)
                {
                    TryDelete(file);
                    continue;
                }
                if (sent >= MaxReportsPerStart)
                    continue;

                var claimed = file + ClaimedMarker + Environment.ProcessId;
                try
                {
                    File.Move(file, claimed);
                }
                catch (IOException)
                {
                    continue; // Another server claimed it first.
                }

                var record = Read(claimed);
                if (record is not null && nowUtc - record.CrashedAtUtc <= MaxAge)
                {
                    send(record);
                    sent++;
                }
                TryDelete(claimed);
            }
            catch (Exception)
            {
                // One bad file must not stop the others.
            }
        }
        return sent;
    }

    private static ServerCrashRecord? Read(string path)
    {
        try
        {
            var record = JsonSerializer.Deserialize<ServerCrashRecord>(File.ReadAllText(path), JsonOptions);
            return record?.SchemaVersion == ServerCrashRecord.CurrentSchemaVersion ? record : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best-effort; retried on the next start.
        }
    }
}
