<#
.SYNOPSIS
  Stops the two-agent multinode dev setup started by start.ps1 (MN-042). State stays in artifacts/dev-multinode, so the
  next start.ps1 reuses the CA and both enrollments (use start.ps1 -Reset for a clean slate).
#>
[CmdletBinding()]
param([string]$Root)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Root) { $Root = Join-Path $repo 'artifacts/dev-multinode' }
$pids = Join-Path $Root 'pids'
if (-not (Test-Path $pids)) {
    Write-Host 'Nothing to stop (no pids file).'
    return
}

# Agents first, then the master.
$entries = @(Get-Content $pids | Where-Object { $_ -match '\S' } | ForEach-Object { $name, $id = $_ -split ' '; [pscustomobject]@{ Name = $name; Id = [int]$id } })
foreach ($entry in @($entries | Where-Object Name -ne 'master') + @($entries | Where-Object Name -eq 'master')) {
    $process = Get-Process -Id $entry.Id -ErrorAction SilentlyContinue
    if ($process -and $process.ProcessName -match '^dotnet') {
        Stop-Process -Id $entry.Id -Force
        Write-Host "stopped $($entry.Name) (pid $($entry.Id))"
    }
    else {
        Write-Host "$($entry.Name) (pid $($entry.Id)) is not running"
    }
}

Remove-Item $pids
