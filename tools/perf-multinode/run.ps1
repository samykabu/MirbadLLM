# MN-046 performance harness (opt-in test, Category=Perf): builds Release and runs the full harness once.
# Writes perf-<os>-<arch>.json and perf-<os>-<arch>.md to -OutDir (default docs/multinode/perf); the Markdown is
# generated from the JSON by the harness. Everything runs in one process on loopback against fakes: the numbers are
# a dev-box measurement, not the DGX acceptance run (design §14 P1). Do not run on the DGX while a CI build runs.
param([string]$OutDir = 'docs/multinode/perf')

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
Push-Location $root
try {
    dotnet build MirbadLLM.slnx -c Release -v q
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $command = 'pwsh tools/perf-multinode/run.ps1'
    if ($PSBoundParameters.ContainsKey('OutDir')) { $command += " -OutDir $OutDir" }
    $env:MIRBADLLM_PERF = '1'
    # Absolute: the xunit runner makes the test assembly's folder the working directory.
    $env:MIRBADLLM_PERF_OUT = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $root $OutDir }
    $env:MIRBADLLM_PERF_COMMAND = $command
    dotnet artifacts/bin/MirbadLLM.UnitTests/release/MirbadLLM.UnitTests.dll `
        -method 'MirbadLLM.UnitTests.Multinode.Perf.PerfHarnessTests.FullRunWritesJsonAndMarkdownReport'
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
