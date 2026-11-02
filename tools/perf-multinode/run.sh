#!/usr/bin/env bash
# MN-046 performance harness (opt-in test, Category=Perf): builds Release and runs the full harness once.
# Writes perf-<os>-<arch>.json and perf-<os>-<arch>.md to $1 (default docs/multinode/perf); the Markdown is generated
# from the JSON by the harness. Everything runs in one process on loopback against fakes: the numbers are a dev-box
# measurement, not the DGX acceptance run (design §14 P1). Do not run on the DGX while a CI build runs.
set -euo pipefail

cd "$(dirname "$0")/../.."
out_dir="${1:-docs/multinode/perf}"
command="tools/perf-multinode/run.sh${1:+ $1}"
# Absolute: the xunit runner makes the test assembly's folder the working directory.
case "$out_dir" in /*) ;; *) out_dir="$(pwd)/$out_dir" ;; esac

dotnet build MirbadLLM.slnx -c Release -v q
MIRBADLLM_PERF=1 MIRBADLLM_PERF_OUT="$out_dir" MIRBADLLM_PERF_COMMAND="$command" \
  dotnet artifacts/bin/MirbadLLM.UnitTests/release/MirbadLLM.UnitTests.dll \
  -method 'MirbadLLM.UnitTests.Multinode.Perf.PerfHarnessTests.FullRunWritesJsonAndMarkdownReport'
