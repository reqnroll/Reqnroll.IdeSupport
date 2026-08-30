<#
.SYNOPSIS
    Turns Visual Studio's "[Stub]" document-tab marker on or off (issue #533, phase 0).
.DESCRIPTION
    When VS reopens a solution it does not load the restored documents: each window frame is
    created in a pending-initialization state and a placeholder ("stub frame") goes into the
    Running Document Table. The document is only realized when the user selects the tab.

    That is the suspected reason a restored .feature tab gets no LSP features until it is
    clicked: while it is a stub, no document matching the LanguageServerProvider's AppliesTo
    filter has been "opened", so VS never activates the provider.

    Setting StubTabTitleFormatString makes the state visible: every tab whose document is not
    yet initialized renders as "MyFeature.feature [Stub]". Reopen a solution with a .feature
    file as the foreground tab and look at the title before touching anything.

    Diagnostic only - it changes nothing but the tab caption, and -Disable removes the value.
.PARAMETER Disable
    Remove the setting instead of adding it.
.PARAMETER Format
    The title format. Must contain {0} (the real caption). Defaults to '{0} [Stub]'.
.PARAMETER Hive
    Explicit VS registry hive name(s), e.g. '17.0_49c9df33Exp'. Defaults to every experimental
    hive found under HKCU:\Software\Microsoft\VisualStudio (main hives are left alone unless
    named explicitly).
.EXAMPLE
    .\tools\Set-StubTabTitles.ps1
    .\tools\Set-StubTabTitles.ps1 -Disable
    .\tools\Set-StubTabTitles.ps1 -Hive '17.0_49c9df33Exp'
.LINK
    https://learn.microsoft.com/visualstudio/extensibility/internals/delayed-document-loading
#>
param(
    [switch]$Disable,
    [string]$Format = '{0} [Stub]',
    [string[]]$Hive
)

$ErrorActionPreference = 'Stop'

$vsRoot = 'HKCU:\Software\Microsoft\VisualStudio'

if (-not $Hive) {
    $Hive = Get-ChildItem $vsRoot |
        Where-Object { $_.PSChildName -match '^\d+\.\d+_[0-9a-f]+Exp$' } |
        Select-Object -ExpandProperty PSChildName
}

if (-not $Hive) {
    Write-Warning "No experimental VS hives found under $vsRoot. Launch the experimental instance once, or pass -Hive explicitly."
    return
}

if (-not $Disable -and $Format -notlike '*{0}*') {
    throw "-Format must contain {0} (the document's real caption); got '$Format'."
}

foreach ($h in $Hive) {
    $key = Join-Path $vsRoot "$h\BackgroundSolutionLoad"

    if ($Disable) {
        if (Test-Path $key) {
            Remove-ItemProperty -Path $key -Name 'StubTabTitleFormatString' -ErrorAction SilentlyContinue
        }
        Write-Host "[$h] StubTabTitleFormatString removed."
        continue
    }

    if (-not (Test-Path $key)) {
        New-Item -Path $key -Force | Out-Null
    }

    New-ItemProperty -Path $key -Name 'StubTabTitleFormatString' -Value $Format -PropertyType String -Force | Out-Null
    Write-Host "[$h] StubTabTitleFormatString = '$Format'"
}

Write-Host ''
Write-Host 'Restart the affected Visual Studio instance for the change to take effect.'
