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
   the in-app release notes, and a [release](#cutting-a-release) into the notes on its GitHub page.

`main` is protected. A pull request needs a passing **Build and test** check and a maintainer's review before it is
merged.

A change that touches only Markdown files, such as the README or anything under `docs/`, skips the build, the tests
and the deploy. One short job looks at the changed files and the other jobs report as skipped, which still satisfies
the required check. The Markdown under `deploy/` is the exception: the agent READMEs ship inside the agent bundles, so
a change there runs everything.

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

## Cutting a release

A release is a tag `v<major>.<minor>.<patch>` on a commit of `main`. Pushing the tag starts
[.github/workflows/release.yml](.github/workflows/release.yml) on the Linux runner. The workflow:

1. Checks that the tagged commit is on `main`.
2. Collects the release notes from the pull requests merged since the previous release.
3. Builds the master, agent and demo images for linux/amd64 and linux/arm64 with
   [deploy/release.sh](deploy/release.sh).
4. Starts the demo image and waits until its fake fleet is up and reports the release's version.
5. Pushes the three images to `ghcr.io/samykabu` as `mirbadllm`, `mirbadllm-agent` and `mirbadllm-demo`.
6. Creates the GitHub Release with the notes and attaches `compose.release.yaml` and `env.example`, the two files
   an installation downloads.

Nothing is pushed before step 4 passes.

### Versions and tags

- The tag without its `v` is the version. Release `v1.2.0` pushes each image as `1.2.0`, `1.2` and `latest`.
- A tag with a suffix, such as `v1.2.0-rc.1`, is a prerelease. Its images get the tag `1.2.0-rc.1` and no other:
  `1.2` and `latest` do not move. The GitHub Release is marked as a prerelease.
- The binaries report `<version>+<commit>`, for example `1.2.0+826bc75fc222`, and the sidebar shows `1.2.0 · 826bc75`.
  The commit is still what identifies a build: the master compares it with each agent's and names the agent bundles
  after it. A build that is not a release, such as the deploy of `main` or a local build, keeps reporting
  `1.0.0+<commit>`.
- Choose the number by what changes for an operator: patch for fixes, minor for new features, major when an upgrade
  needs a manual step or an agent has to be updated before it works with the new master.

### Steps

1. Read what the release will contain: `git log --oneline <previous tag>..origin/main`. The notes are the
   `## Release notes` sections of those pull requests, read when the workflow runs. To correct a note, edit the pull
   request's description before you tag.
2. Do a dry run: **Actions > release > Run workflow** on `main`, or `gh workflow run release.yml --ref main`. It
   builds both architectures, tests the demo image and writes the notes to the run's summary. It pushes nothing and
   creates no release.
3. Tag the commit and push the tag:

   ```bash
   git switch main && git pull
   git tag -a v1.2.0 -m "MirbadLLM 1.2.0"
   git push origin v1.2.0
   ```

4. When the run has finished, check the result: the release page shows the notes and the two files, and
   `docker run --rm -p 127.0.0.1:8080:8080 ghcr.io/samykabu/mirbadllm-demo:1.2.0` starts the demo with `1.2.0` in
   the sidebar.

`CHANGELOG.md` is not part of a release. The workflow neither reads nor changes it.

If the run fails for a reason outside the code, such as a missing tool on the runner or a registry error, fix the
cause and run the failed job again from the run's page. A second run pushes the same tags again and replaces the
release's notes and files, so notes edited by hand on the release page are lost. If the tagged commit itself is
wrong and its images were pushed, leave the tag alone and release the fix as the next patch version. A tag whose
images were never pushed can be deleted with `git push origin :v1.2.0`.

### One-time setup

The runner needs these tools, installed once on its host and available to the runner's user. The workflow checks for
them first and stops with a message when one is missing.

- Docker with the buildx plugin (`docker buildx version`).
- `python3`.
- The GitHub CLI, `gh`.

The first run creates a BuildKit builder container named `mirbadllm-release`, which pulls the `moby/buildkit` image
once. The builder is limited to 16 GiB of memory, like the CI build container, and is kept between runs for its
cache. `docker buildx prune --builder mirbadllm-release` frees the cache's disk space.

The repository owner does these by hand:

- **Make the packages public, once, after the first release.** A new package on ghcr.io is private, and
  `docker pull` then fails for everyone else. For each of `mirbadllm`, `mirbadllm-agent` and `mirbadllm-demo`: open
  the package from the owner's **Packages** tab, then **Package settings > Change visibility > Public**. GitHub does
  not let a public package become private again.
- **Check the package's access if a later push is refused.** The first push links each package to this repository.
  If a push fails with `denied`, open **Package settings > Manage Actions access** and give this repository the
  **Write** role.
- **Optional: restrict who can create `v*` tags** with a tag ruleset (**Settings > Rules > Rulesets**). Anyone who
  can push a tag can publish a release.

The workflow uses the job's own `GITHUB_TOKEN` with `contents: write`, `packages: write` and `pull-requests: read`.
It logs the runner's Docker in to ghcr.io for the push and logs it out at the end of the job.

## Reporting bugs and asking for features

Use the issue templates. For a bug, describe what you did, what you expected and what happened, and include the
MirbadLLM version (shown in the sidebar), the node's operating system and runtime, and the relevant log lines. If a
model failed to start, the last 20 lines of its **Logs** tab usually show the cause. If a benchmark result looks
wrong, note the model, the quantization and the concurrency.

## Documentation style

- Headings in sentence case.
- UI labels exactly as displayed, in bold: **Add model**.
- State the fact and the condition it depends on. Measured numbers come with what was measured and where.
