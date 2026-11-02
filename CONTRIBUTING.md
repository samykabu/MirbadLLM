# Contributing to MirbadLLM

Thanks for considering a contribution. This guide covers the development setup, the commands that build and test the
code, how a change becomes a pull request, and what CI does with it. Please read the
[code of conduct](CODE_OF_CONDUCT.md) first.

## Before you start

- For anything larger than a small fix, open an issue first and describe the problem and the change you have in
  mind. It saves you from building something that does not fit.
- Read [docs/architecture.md](docs/architecture.md). Its behaviour contracts are what the tests check, and a change
  that breaks one needs a reason.
- Security problems go through private vulnerability reporting, not issues. See [SECURITY.md](SECURITY.md).

## Development setup

### Prerequisites

- The .NET 10 SDK, 10.0.100 or a later feature band. `global.json` pins the version.
- Git.
- Optional: PowerShell 7 or Bash for the development fleet scripts, and Docker for the container and packaging
  tests that need it (they skip themselves without it).

No GPU is needed. The development fleet and the tests use fake runtimes.

### Build and test

From the repository root:

```bash
dotnet build MirbadLLM.slnx -c Debug                     # warnings are errors: the build must report 0 warnings
dotnet test tests/MirbadLLM.UnitTests --no-build         # the full unit suite (xUnit v3, Microsoft.Testing.Platform)
```

The build is complete when it reports `0 Warning(s)` and `0 Error(s)`. The suite takes a few minutes; it includes the
`Chaos` category (multi-node fault injection with deterministic seeds). The `Perf` harness is opt-in: set
`MIRBADLLM_PERF=1` or run `tools/perf-multinode/run.ps1` or `run.sh`.

You can also run the test assembly directly, which is what CI does:

```bash
dotnet artifacts/bin/MirbadLLM.UnitTests/debug/MirbadLLM.UnitTests.dll
dotnet artifacts/bin/MirbadLLM.UnitTests/debug/MirbadLLM.UnitTests.dll -class MirbadLLM.UnitTests.Web.TabsTests   # one class
```

Filter with `-class`, `-namespace` or `-method` while you work on one area, and run the whole suite before you push.

Some tests run only on their own operating system (the Windows service and installer tests, the macOS process host and
launchd tests) and skip themselves elsewhere. On Windows, a few tests that reserve local TCP ports or replace files
can fail when another program holds the port or the file (an antivirus scan, for example); run them again on their
own before you investigate.

### Run the app

The development fleet runs a master and two fake-runtime agents on loopback:

```bash
pwsh tools/dev-multinode/start.ps1 -Reset      # Windows, or PowerShell 7 anywhere
tools/dev-multinode/start.sh --reset           # Linux and macOS
```

The UI is at `http://127.0.0.1:5080/`. Stop it with `tools/dev-multinode/stop.ps1` or `stop.sh`. The
[setup guide](docs/setup.md#development-fleet) explains what each process does.

## Conventions

- .NET 10, nullable reference types on, `TreatWarningsAsErrors`, `latest-recommended` analyzers. A change that adds a
  warning does not build.
- Central package management (`Directory.Packages.props`) with exact versions. Do not add a package without
  discussing it in an issue first; almost everything here is done with the BCL and ASP.NET Core.
- No Docker client library: the code talks to the Docker Engine API with `HttpClient` over the Unix socket, a named
  pipe or TCP.
- Async all the way, a `CancellationToken` on every I/O call, `System.Text.Json` with web defaults.
- Tests use xUnit v3 with hand-written fakes and no mocking library, one folder per area in
  `tests/MirbadLLM.UnitTests/`. UI components are tested with bUnit.
- `src/MirbadLLM.Core` holds the contracts every project shares. Changes there affect everything, so explain them in
  the pull request.
- UI work follows [DESIGN.md](DESIGN.md): tokens, typography, both themes, WCAG 2.2 AA.

## Branches, commits and pull requests

1. Fork the repository (or create a branch, if you have write access) from `main`. Name the branch after the change,
   for example `fix/node-detail-overflow` or `feature/gguf-import`.
2. Make the change with its tests. Every behaviour change needs a test that fails without it.
3. Write commit messages in the imperative mood with a short subject line, for example
   `Keep fleet status warnings inside narrow screens`. Explain the why in the body when it is not obvious.
4. Run the build and the full suite locally.
5. Open a pull request against `main` and fill in the template. Add a `## Release notes` section with `### Added`,
   `### Fixed` or `### Changed` entries when users will notice the change: the deploy job turns these sections into
   the in-app release notes.

`main` is protected. A pull request needs a passing **Build and test** check and a maintainer's review before it is
merged.

## How CI treats pull requests

CI runs on the project's own self-hosted runners: a Linux arm64 machine for the build, the tests and the packaging
lint, and optional Windows and macOS runners for the tests that only run there. A self-hosted runner must never run
code from a fork, because that code would run on a maintainer's machine. Every job in
[.github/workflows/ci.yml](.github/workflows/ci.yml) is therefore skipped for pull requests from forks.

That means a pull request from a fork shows no CI results at first. Once a maintainer has reviewed the change, they
push your branch to this repository, which runs the full CI on it, and report the results on your pull request. Run
the build and the tests locally before you ask for review, so this round trip finds nothing new.

Maintainers configure CI with these repository variables (**Settings > Secrets and variables > Actions >
Variables**), all optional:

| Variable | Effect |
|---|---|
| `CI_DOTNET_SDK_IMAGE` | Container image of the Linux build (default `mcr.microsoft.com/dotnet/sdk:10.0`) |
| `CI_NUGET_CACHE_DIR` | Folder on the Linux runner's host used as the NuGet cache (default `/tmp/mirbadllm-ci-nuget`) |
| `WINDOWS_CI` | `1` runs the Windows leg on a runner labelled `mirbadllm-win` |
| `MAC_CI` | `1` runs the macOS leg on a runner labelled `mirbadllm-mac` |
| `DEPLOY_ENABLED` | `1` deploys `main` to the Linux runner's host after the tests pass |

No secrets are needed beyond the automatic `GITHUB_TOKEN`.

## Reporting bugs and asking for features

Use the issue templates. For a bug, describe what you did, what you expected and what happened, and include the
MirbadLLM version (shown in the sidebar), the node's operating system and runtime, and the relevant log lines. If a
model failed to start, the last 20 lines of its **Logs** tab usually show the cause. If a benchmark result looks
wrong, note the model, the quantization and the concurrency.

## Documentation style

- Headings in sentence case.
- UI labels exactly as displayed, in bold: **Add model**.
- State the fact and the condition it depends on. Measured numbers come with what was measured and where.
