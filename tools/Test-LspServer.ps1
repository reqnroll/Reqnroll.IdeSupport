#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Test harness for Reqnroll LSP Server � semantic tokens smoke test.

.DESCRIPTION
    1. Builds the server (debug).
    2. Spawns the server process (stdin/stdout transport).
    3. Sends: initialize -> initialized -> textDocument/didOpen (dummy .feature) ->
              textDocument/semanticTokens/full
    4. Decodes the 5-int LSP semantic token tuples and prints a human-readable table.

.EXAMPLE
    pwsh -File tools\Test-LspServer.ps1
    pwsh -File tools\Test-LspServer.ps1 -SkipBuild -Verbose
#>
param(
    [switch] $SkipBuild,
    [int]    $TimeoutSec = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---- Paths ------------------------------------------------------------------

$repoRoot  = Split-Path $PSScriptRoot -Parent
$csproj    = Join-Path $repoRoot "src\LSP\Reqnroll.IdeSupport.LSP.Server\Reqnroll.IdeSupport.LSP.Server.csproj"
$serverExe = Join-Path $repoRoot "src\LSP\Reqnroll.IdeSupport.LSP.Server\bin\Debug\net10.0\Reqnroll.IdeSupport.LSP.Server.exe"

# ---- Build ------------------------------------------------------------------

if (-not $SkipBuild) {
    Write-Host "Building server..." -ForegroundColor Cyan
    dotnet build $csproj -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    Write-Host "Build OK." -ForegroundColor Green
}

if (-not (Test-Path $serverExe)) { throw "Server executable not found: $serverExe" }

# ---- Inline C# LSP client ---------------------------------------------------
# All blocking stream I/O lives in a compiled C# class to avoid PowerShell
# cross-thread scoping limitations with ConcurrentQueue<T>.

Add-Type -Language CSharp @'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public static class LspTestClient
{
    private static Process   _proc;
    private static Stream    _stdin;
    private static Stream    _stdout;
    private static readonly ConcurrentQueue<JsonDocument> _inbox = new ConcurrentQueue<JsonDocument>();
    private static Thread    _reader;
    private static volatile bool _stop;

    public static void Start(string exePath)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute       = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        _proc   = Process.Start(psi);
        _stdin  = _proc.StandardInput.BaseStream;
        _stdout = _proc.StandardOutput.BaseStream;

        // Drain stderr on a background task so it never deadlocks the server.
        Task.Run(() => { try { _proc.StandardError.ReadToEnd(); } catch { } });

        _stop   = false;
        _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "LspReader" };
        _reader.Start();

        // Give the server time to initialise its DI container before we send anything.
        Thread.Sleep(600);
    }

    public static void Stop()
    {
        _stop = true;
        try { if (!_proc.HasExited) _proc.Kill(); } catch { }
        if (_reader != null) _reader.Join(2000);
        if (_proc   != null) _proc.Dispose();
    }

    public static bool HasExited { get { return _proc == null || _proc.HasExited; } }

    public static void Send(string json, bool verbose)
    {
        if (verbose) Console.Error.WriteLine("-> " + json);
        byte[] body   = Encoding.UTF8.GetBytes(json);
        byte[] header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
        lock (_stdin)
        {
            _stdin.Write(header, 0, header.Length);
            _stdin.Write(body,   0, body.Length);
            _stdin.Flush();
        }
    }

    /// Blocks until the response whose "id" == waitId arrives (notifications skipped).
    /// Server->client requests (have both "id" and "method") are also skipped.
    /// Returns null on timeout.
    public static JsonDocument WaitForResponse(int waitId, int timeoutMs, bool verbose)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            JsonDocument doc;
            if (_inbox.TryDequeue(out doc))
            {
                if (verbose) Console.Error.WriteLine("<- " + doc.RootElement.ToString());
                JsonElement idProp;
                int id;
                // A true response has "id" but NO "method" field.
                if (doc.RootElement.TryGetProperty("id", out idProp)      &&
                    !doc.RootElement.TryGetProperty("method", out _)       &&
                    idProp.TryGetInt32(out id) && id == waitId)
                    return doc;
                doc.Dispose(); // notification, server->client request, or different id
            }
            else
            {
                Thread.Sleep(20);
            }
        }
        return null;
    }

    // ---- Reader thread ------------------------------------------------------

    private static void ReaderLoop()
    {
        List<byte> acc = new List<byte>(65536);
        byte[] buf     = new byte[4096];
        byte[] sep     = new byte[] { 13, 10, 13, 10 }; // \r\n\r\n

        while (!_stop)
        {
            int n;
            try { n = _stdout.Read(buf, 0, buf.Length); }
            catch { break; }
            if (n == 0) { Thread.Sleep(5); continue; }
            for (int i = 0; i < n; i++) acc.Add(buf[i]);

            while (true)
            {
                int sepIdx = IndexOf(acc, sep);
                if (sepIdx < 0) break;

                string header = Encoding.ASCII.GetString(acc.ToArray(), 0, sepIdx);
                int bodyLen   = ParseContentLength(header);
                if (bodyLen < 0) { acc.RemoveRange(0, sepIdx + 4); break; }

                int bodyStart = sepIdx + 4;
                if (acc.Count - bodyStart < bodyLen) break; // need more data

                byte[] body = new byte[bodyLen];
                acc.CopyTo(bodyStart, body, 0, bodyLen);
                acc.RemoveRange(0, bodyStart + bodyLen);

                try { _inbox.Enqueue(JsonDocument.Parse(body)); }
                catch { /* skip malformed */ }
            }
        }
    }

    private static int IndexOf(List<byte> haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Count - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static int ParseContentLength(string header)
    {
        foreach (string line in header.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                int val;
                if (int.TryParse(t.Substring(15).Trim(), out val)) return val;
            }
        }
        return -1;
    }
}
'@

# ---- Dummy feature file -----------------------------------------------------

$featureText = @'
Feature: Calculator
  As a user
  I want to add numbers
  So that I get the correct result

  @smoke
  Scenario: Add two numbers
    Given I have entered 50 into the calculator
    And I have entered 70 into the calculator
    When I press add
    Then the result should be 120 on the screen

  Scenario Outline: Add <a> and <b>
    Given I have entered <a> into the calculator
    And I have entered <b> into the calculator
    When I press add
    Then the result should be <result> on the screen

    Examples:
      | a  | b  | result |
      | 1  | 2  | 3      |
      | 10 | 20 | 30     |
'@

$rootUriStr  = "file:///" + ($repoRoot -replace '\\','/')
$featureUri  = $rootUriStr + "/test-dummy.feature"

# ---- Token legend (must match ReqnrollClassificationTypeNames.Ordered) --------

$tokenTypeNames     = @(
    'reqnroll.keyword',
    'reqnroll.tag',
    'reqnroll.description',
    'reqnroll.comment',
    'reqnroll.doc_string',
    'reqnroll.data_table',
    'reqnroll.data_table_header',
    'reqnroll.step_parameter',
    'reqnroll.scenario_outline_placeholder',
    'reqnroll.undefined_step'
)
$tokenModifierNames = @()

function Decode-SemanticTokens([int[]] $data) {
    $rows = [System.Collections.Generic.List[PSCustomObject]]::new()
    $line = 0; $char = 0
    for ($i = 0; $i -lt $data.Count; $i += 5) {
        $dl = $data[$i]; $dc = $data[$i+1]; $len = $data[$i+2]; $ti = $data[$i+3]; $mb = $data[$i+4]
        if ($dl -gt 0) { $char = 0 }
        $line += $dl; $char += $dc
        $typeName = if ($ti -lt $tokenTypeNames.Count) { $tokenTypeNames[$ti] } else { "type$ti" }
        $mods = @(); for ($b = 0; $b -lt $tokenModifierNames.Count; $b++) {
            if ($mb -band (1 -shl $b)) { $mods += $tokenModifierNames[$b] }
        }
        $rows.Add([PSCustomObject]@{
            Line = $line + 1; Char = $char + 1; Length = $len
            TokenType = $typeName; Modifiers = if ($mods) { $mods -join ',' } else { '-' }
        })
    }
    return $rows
}

# ---- Helpers ----------------------------------------------------------------

$verbose = $PSBoundParameters.ContainsKey('Verbose') -and $PSBoundParameters['Verbose']
$msgId   = 0
function Next-Id { return (++$script:msgId) }

function Build-Json([hashtable] $h) { return ($h | ConvertTo-Json -Depth 20 -Compress) }

# ---- Run --------------------------------------------------------------------

Write-Host "`nStarting LSP server..." -ForegroundColor Cyan
[LspTestClient]::Start($serverExe)
if ([LspTestClient]::HasExited) { throw "Server exited immediately." }

$timeoutMs = $TimeoutSec * 1000

try {
    # -- initialize -----------------------------------------------------------
    $initId = Next-Id
    Write-Host "Sending initialize (id=$initId)..." -ForegroundColor DarkCyan
    [LspTestClient]::Send((Build-Json @{
        jsonrpc = "2.0"; id = $initId; method = "initialize"
        params  = @{
            processId        = [System.Diagnostics.Process]::GetCurrentProcess().Id
            rootUri          = $rootUriStr
            capabilities     = @{
                textDocument = @{
                    synchronization = @{ dynamicRegistration = $true }
                    semanticTokens  = @{
                        dynamicRegistration     = $true
                        requests                = @{ full = $true; range = $true }
                        tokenTypes              = $tokenTypeNames
                        tokenModifiers          = $tokenModifierNames
                        formats                 = @("relative")
                        overlappingTokenSupport = $false
                        multilineTokenSupport   = $true
                    }
                }
                workspace = @{
                    workspaceFolders = $true
                    semanticTokens   = @{ refreshSupport = $true }
                }
            }
            workspaceFolders = @(@{ uri = $rootUriStr; name = "Reqnroll.IdeSupport" })
        }
    }), $verbose)

    $initResp = [LspTestClient]::WaitForResponse($initId, $timeoutMs, $verbose)
    if ($null -eq $initResp) { throw "Timed out waiting for initialize response." }

    $si = $initResp.RootElement.GetProperty("result").GetProperty("serverInfo")
    Write-Host ("  Server: {0} {1}" -f $si.GetProperty("name").GetString(),
                                        $si.GetProperty("version").GetString()) -ForegroundColor Green

    # -- initialized ----------------------------------------------------------
    [LspTestClient]::Send((Build-Json @{ jsonrpc = "2.0"; method = "initialized"; params = @{} }), $verbose)
    Start-Sleep -Milliseconds 400

    # -- didOpen --------------------------------------------------------------
    Write-Host "Sending textDocument/didOpen..." -ForegroundColor DarkCyan
    [LspTestClient]::Send((Build-Json @{
        jsonrpc = "2.0"; method = "textDocument/didOpen"
        params  = @{ textDocument = @{ uri = $featureUri; languageId = "Gherkin"; version = 1; text = $featureText } }
    }), $verbose)

    # Allow the server time to parse and cache tokens before we request them.
    Start-Sleep -Milliseconds 1000

    # -- semanticTokens/full --------------------------------------------------
    $tokId = Next-Id
    Write-Host "Sending textDocument/semanticTokens/full (id=$tokId)..." -ForegroundColor DarkCyan
    [LspTestClient]::Send((Build-Json @{
        jsonrpc = "2.0"; id = $tokId; method = "textDocument/semanticTokens/full"
        params  = @{ textDocument = @{ uri = $featureUri } }
    }), $verbose)

    $tokResp = [LspTestClient]::WaitForResponse($tokId, $timeoutMs, $verbose)
    if ($null -eq $tokResp) { throw "Timed out waiting for semanticTokens/full response." }

    $errProp = [System.Text.Json.JsonElement]::new()
    if ($tokResp.RootElement.TryGetProperty("error", [ref]$errProp)) {
        throw "Server returned LSP error: $($errProp.ToString())"
    }

    # result:null serialises as an absent "result" key in System.Text.Json
    $resultProp = [System.Text.Json.JsonElement]::new()
    if (-not $tokResp.RootElement.TryGetProperty("result", [ref]$resultProp) -or
        $resultProp.ValueKind -eq [System.Text.Json.JsonValueKind]::Null) {
        Write-Host "`nWARNING: Server returned null for semanticTokens/full (no tokens cached yet)." -ForegroundColor Yellow
        exit 0
    }
    $result = $resultProp

    $dataProp = [System.Text.Json.JsonElement]::new()
    if (-not $result.TryGetProperty("data", [ref]$dataProp) -or $dataProp.GetArrayLength() -eq 0) {
        Write-Host "`nWARNING: Server returned empty token data." -ForegroundColor Yellow
        exit 0
    }

    $intData = [int[]]($dataProp.EnumerateArray() | ForEach-Object { $_.GetInt32() })
    $decoded = Decode-SemanticTokens $intData

    Write-Host ("`n--- Semantic Tokens ({0} tokens, {1} ints) ---" -f $decoded.Count, $intData.Count) -ForegroundColor Cyan
    $decoded | Format-Table -AutoSize
    Write-Host "Done." -ForegroundColor Green
}
finally {
    # -- shutdown -------------------------------------------------------------
    try {
        $shutId = Next-Id
        [LspTestClient]::Send((Build-Json @{ jsonrpc = "2.0"; id = $shutId; method = "shutdown"; params = $null }), $false)
        $null = [LspTestClient]::WaitForResponse($shutId, 3000, $false)
        [LspTestClient]::Send((Build-Json @{ jsonrpc = "2.0"; method = "exit"; params = $null }), $false)
        Start-Sleep -Milliseconds 200
    } catch { }
    [LspTestClient]::Stop()
}
