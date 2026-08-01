#nullable enable

using System;
using System.Collections.Concurrent;

namespace Reqnroll.IdeSupport.LSP.Core.Rename;

/// <summary>
/// Tracks pending rename sessions (multi-attribute picker flow).
/// A session is established when the client calls reqnroll/selectRenameTarget
/// and consumed (or expired) on the subsequent textDocument/rename.
/// </summary>
public class RenameSessionManager
{
    private static readonly TimeSpan SessionDuration = TimeSpan.FromSeconds(30);

    private readonly Func<DateTime> _utcNow;

    // Key: (uri, documentVersion) → (attributeIndex, expiresAt)
    private readonly ConcurrentDictionary<(string Uri, int Version), (int AttributeIndex, DateTime ExpiresAt)> _sessions = new();

    /// <summary>Initializes a new instance of the <see cref="RenameSessionManager"/> class using the real system clock.</summary>
    public RenameSessionManager() : this(() => DateTime.UtcNow)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RenameSessionManager"/> class with an injectable clock, allowing tests to control session expiry.</summary>
    public RenameSessionManager(Func<DateTime> utcNow)
    {
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>Stores a pending rename session with a 30-second expiry.</summary>
    public void SetSession(string uri, int version, int attributeIndex)
    {
        var key = (NormalizeUri(uri), version);
        _sessions[key] = (attributeIndex, _utcNow() + SessionDuration);
        Cleanup();
    }

    /// <summary>
    /// Attempts to consume a pending session. Returns true if a valid (non-expired)
    /// session exists for the given URI+version, and outputs the attributeIndex.
    /// The session is removed on successful consumption.
    /// </summary>
    public bool TryConsume(string uri, int version, out int attributeIndex)
    {
        attributeIndex = 0;
        Cleanup();

        var key = (NormalizeUri(uri), version);
        if (_sessions.TryRemove(key, out var entry))
        {
            if (entry.ExpiresAt > _utcNow())
            {
                attributeIndex = entry.AttributeIndex;
                return true;
            }
        }
        return false;
    }

    private static string NormalizeUri(string uri)
    {
        // Decode percent-encoding before lowercasing. VS Code's vscode.Uri.toString() percent-
        // encodes the Windows drive-letter colon (file:///c%3A/Users/...), which is what
        // SelectRenameTargetParams.Uri carries verbatim from the client. The subsequent
        // textDocument/rename request's URI is re-serialized server-side through OmniSharp's
        // DocumentUri, which does not use that encoding (file:///c:/Users/...). Without
        // unescaping first, those two representations of the identical file normalize to
        // different keys, TryConsume always misses, and HandleRenameAsync silently falls back
        // to picking the first ambiguous candidate regardless of what the user selected.
        try
        {
            uri = Uri.UnescapeDataString(uri);
        }
        catch (Exception)
        {
            // Not a well-formed percent-encoded string — fall back to the raw value.
        }

        // Normalize URI casing so that file:///C:/Users/... and file:///c:/Users/...
        // map to the same key. On Windows, file paths are case-insensitive.
        return uri.ToLowerInvariant();
    }

    /// <summary>Removes expired sessions on a best-effort basis.</summary>
    private void Cleanup()
    {
        var now = _utcNow();
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.ExpiresAt <= now)
                _sessions.TryRemove(kvp.Key, out _);
        }
    }
}
