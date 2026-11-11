# Master image (router + admin API + Blazor UI + enroll listener). Built natively on the DGX Spark (linux/arm64); the
# build stage runs on the build machine's platform and publishes for the target architecture, so
#   docker buildx build --platform linux/amd64,linux/arm64 -f deploy/Dockerfile .
# also works without QEMU (design §11.1: the same image for the DGX and, in P5, a Kubernetes cluster).
# Base images are the official mcr.microsoft.com/dotnet/{sdk,aspnet} images. To pull them through a registry mirror,
# edit the FROM lines (or tag the mirror's copies with these names on the build machine).
# The last stage is the master image. "--target demo" builds the demo image instead (deploy/demo; docs/setup.md, "Demo").
# deploy/release.sh builds both for a release.
ARG DOTNET_TAG=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_TAG} AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props MirbadLLM.slnx ./
COPY src/MirbadLLM.Core/MirbadLLM.Core.csproj src/MirbadLLM.Core/
COPY src/MirbadLLM.Agent.Contracts/MirbadLLM.Agent.Contracts.csproj src/MirbadLLM.Agent.Contracts/
COPY src/MirbadLLM.Pki/MirbadLLM.Pki.csproj src/MirbadLLM.Pki/
COPY src/MirbadLLM.NodeRuntime/MirbadLLM.NodeRuntime.csproj src/MirbadLLM.NodeRuntime/
COPY src/MirbadLLM.Orchestration/MirbadLLM.Orchestration.csproj src/MirbadLLM.Orchestration/
COPY src/MirbadLLM.Router/MirbadLLM.Router.csproj src/MirbadLLM.Router/
COPY src/MirbadLLM.Web/MirbadLLM.Web.csproj src/MirbadLLM.Web/
# TARGETARCH is set by BuildKit (amd64/arm64); without it the build machine's own architecture is used.
RUN case "${TARGETARCH:-$(uname -m)}" in \
        amd64|x86_64) echo x64 ;; \
        arm64|aarch64) echo arm64 ;; \
        *) echo "unsupported architecture ${TARGETARCH:-$(uname -m)}" >&2; exit 1 ;; \
    esac > /tmp/arch && \
    dotnet restore src/MirbadLLM.Web/MirbadLLM.Web.csproj -a "$(cat /tmp/arch)"
COPY src/ src/
# No --no-restore: the SDK only adds the Blazor framework-assets package (blazor.web.js) once it sees
# the .razor files, which the csproj-only restore above cannot.
# /layout/bootstrap becomes the bootstrap directory shared with the DGX agent (0700, uid 1654; plan §3.6 W4 notes).
# The commit (ci-deploy-multinode.sh passes it): the controller's informational version becomes 1.0.0+<commit>, which the
# portal shows and compares with each agent's. Declared here, after the restore, so a new commit keeps that layer cached.
ARG SOURCE_REVISION=unknown
# The release version (deploy/release.sh passes the tag without its "v"): 1.0.0+<commit> becomes <version>+<commit>. No
# publish line names it: a build argument is in the environment of every RUN after it, and MSBuild reads an environment
# variable as the property of that name, here Version. Empty (every other build) keeps the SDK's default, 1.0.0. Builds
# are still compared by the commit alone, so an agent and a master of one commit match whatever version each carries.
ARG Version=
RUN set -eu; \
    case "${SOURCE_REVISION}" in *[!A-Za-z0-9._-]* | "") echo "SOURCE_REVISION must be letters, digits, '.', '_' or '-'" >&2; exit 1 ;; esac; \
    dotnet publish src/MirbadLLM.Web/MirbadLLM.Web.csproj -c Release -a "$(cat /tmp/arch)" --self-contained false \
        -p:SourceRevisionId="${SOURCE_REVISION}" -o /app; \
    mkdir -p /layout/bootstrap

# Linux agent bundles for PCs (served anonymously at GET /agent/bundles/{rid}.tar.gz): both architectures, built on the
# build machine's platform (a cross-RID publish needs no emulation) with the settings and layout of
# "install-agent.sh package" -- the script itself makes each tarball from the published binary. One RID at a time, so the
# build's memory stays that of a single publish. SOURCE_REVISION (deploy/ci-deploy-multinode.sh passes the commit) names
# the bundle and goes into BUILD-INFO. The same stage makes the Windows bundle (win-x64.zip, below).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_TAG} AS agent-bundles
ARG SOURCE_REVISION=unknown
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/MirbadLLM.Core/MirbadLLM.Core.csproj src/MirbadLLM.Core/
COPY src/MirbadLLM.Agent.Contracts/MirbadLLM.Agent.Contracts.csproj src/MirbadLLM.Agent.Contracts/
COPY src/MirbadLLM.Pki/MirbadLLM.Pki.csproj src/MirbadLLM.Pki/
COPY src/MirbadLLM.NodeRuntime/MirbadLLM.NodeRuntime.csproj src/MirbadLLM.NodeRuntime/
COPY src/MirbadLLM.Agent/MirbadLLM.Agent.csproj src/MirbadLLM.Agent/
COPY src/MirbadLLM.HostAgent/MirbadLLM.HostAgent.csproj src/MirbadLLM.HostAgent/
RUN for rid in linux-x64 linux-arm64 win-x64 osx-arm64; do \
        dotnet restore src/MirbadLLM.Agent/MirbadLLM.Agent.csproj -r "$rid" -p:SelfContained=true -p:PublishSingleFile=true || exit 1; \
    done; \
    dotnet restore src/MirbadLLM.HostAgent/MirbadLLM.HostAgent.csproj -r osx-arm64 -p:SelfContained=true -p:PublishSingleFile=true
COPY src/MirbadLLM.Core/ src/MirbadLLM.Core/
COPY src/MirbadLLM.Agent.Contracts/ src/MirbadLLM.Agent.Contracts/
COPY src/MirbadLLM.Pki/ src/MirbadLLM.Pki/
COPY src/MirbadLLM.NodeRuntime/ src/MirbadLLM.NodeRuntime/
COPY src/MirbadLLM.Agent/ src/MirbadLLM.Agent/
COPY deploy/agent/agent.linux.example.json deploy/agent/
COPY deploy/agent/linux/install-agent.sh deploy/agent/linux/mirbadllm-agent.service deploy/agent/linux/README.md deploy/agent/linux/
# The root helper behind pushed agent updates (docs/multinode/agent-updates.md), carried by the Linux and macOS bundles.
COPY deploy/agent/agent-update-helper.sh deploy/agent/
# The release version of every bundle, read by MSBuild from the environment as in the build stage. After the restore, so
# a new version keeps that layer cached.
ARG Version=
# The publish line is install-agent.sh's own (publish_from_repo): self-contained, single file, no symbols.
RUN set -eu; \
    case "${SOURCE_REVISION}" in *[!A-Za-z0-9._-]* | "") echo "SOURCE_REVISION must be letters, digits, '.', '_' or '-'" >&2; exit 1 ;; esac; \
    mkdir -p /bundles; \
    for rid in linux-x64 linux-arm64; do \
        dotnet publish src/MirbadLLM.Agent/MirbadLLM.Agent.csproj -c Release -r "$rid" \
            --self-contained true -p:PublishSingleFile=true -p:DebugType=none \
            -p:SourceRevisionId="${SOURCE_REVISION}" -o "/publish/$rid" --nologo -v quiet; \
        printf 'revision=%s\nrid=%s\nbuilt=%s\n' "${SOURCE_REVISION}" "$rid" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >"/publish/$rid/BUILD-INFO"; \
        bash deploy/agent/linux/install-agent.sh package --published-dir "/publish/$rid" --rid "$rid" --output "/bundles/$rid.tar.gz"; \
        rm -rf "/publish/$rid"; \
    done; \
    ls -l /bundles
# Windows agent bundle (served at GET /agent/bundles/win-x64.zip; design-p3p4 §A.4.5, MN-218): the same publish settings,
# cross-compiled for win-x64 on the build platform (no Windows runner), and one folder mirbadllm-agent-{revision}-win-x64
# with the single-file exe (only the exe: the publish folder's web.config and IIS module are not the agent's),
# install-agent.ps1, the example configuration, the pinned llama.cpp lock file, BUILD-INFO and the README when there is
# one. The SDK image's PowerShell writes the zip (the image has no zip tool). A second folder with the pre-rename name
# (spark-serve-agent-{revision}-win-x64) holds only install-agent.ps1 and its two data files: the update helper installed
# on a pre-rename node runs the installer from there, and the installer unpacks the bundle again and finds the exe itself.
COPY deploy/agent/agent.windows.example.json deploy/agent/
COPY deploy/agent/windows/ deploy/agent/windows/
RUN set -eu; \
    case "${SOURCE_REVISION}" in *[!A-Za-z0-9._-]* | "") echo "SOURCE_REVISION must be letters, digits, '.', '_' or '-'" >&2; exit 1 ;; esac; \
    dotnet publish src/MirbadLLM.Agent/MirbadLLM.Agent.csproj -c Release -r win-x64 \
        --self-contained true -p:PublishSingleFile=true -p:DebugType=none \
        -p:SourceRevisionId="${SOURCE_REVISION}" -o /publish/win-x64 --nologo -v quiet; \
    folder="/stage/mirbadllm-agent-${SOURCE_REVISION}-win-x64"; \
    mkdir -p "$folder"; \
    cp /publish/win-x64/mirbadllm-agent.exe deploy/agent/windows/install-agent.ps1 deploy/agent/windows/llama-cpp.lock.json \
        deploy/agent/agent.windows.example.json "$folder/"; \
    if [ -f deploy/agent/windows/README.md ]; then cp deploy/agent/windows/README.md "$folder/"; fi; \
    printf 'revision=%s\nrid=%s\nbuilt=%s\n' "${SOURCE_REVISION}" win-x64 "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >"$folder/BUILD-INFO"; \
    legacy="/stage/spark-serve-agent-${SOURCE_REVISION}-win-x64"; \
    mkdir -p "$legacy"; \
    cp deploy/agent/windows/install-agent.ps1 deploy/agent/windows/llama-cpp.lock.json deploy/agent/agent.windows.example.json "$legacy/"; \
    pwsh -NoLogo -NoProfile -NonInteractive -Command \
        'Add-Type -AssemblyName System.IO.Compression.ZipFile; [System.IO.Compression.ZipFile]::CreateFromDirectory("/stage", "/bundles/win-x64.zip")'; \
    rm -rf /publish/win-x64 /stage; \
    ls -l /bundles
# macOS agent bundle (served at GET /agent/bundles/osx-arm64.tar.gz; docs/macos/implementation-plan.md, Phase 1): the same
# publish settings, cross-compiled for Apple Silicon here (the SDK signs the single-file binary ad hoc, which is all
# arm64 macOS needs for a LAN install: curl adds no quarantine flag), packaged by the macOS script's portable "package"
# verb into one folder mirbadllm-agent-{revision}-osx-arm64. The macOS hostd (Phase 4, plan M10: the root helper behind
# the Services tab) is published with the same settings and travels in the same folder ("install-agent.sh install-hostd").
COPY deploy/agent/agent.macos.example.json deploy/agent/
COPY deploy/agent/macos/ deploy/agent/macos/
COPY src/MirbadLLM.HostAgent/ src/MirbadLLM.HostAgent/
RUN set -eu; \
    case "${SOURCE_REVISION}" in *[!A-Za-z0-9._-]* | "") echo "SOURCE_REVISION must be letters, digits, '.', '_' or '-'" >&2; exit 1 ;; esac; \
    dotnet publish src/MirbadLLM.Agent/MirbadLLM.Agent.csproj -c Release -r osx-arm64 \
        --self-contained true -p:PublishSingleFile=true -p:DebugType=none \
        -p:SourceRevisionId="${SOURCE_REVISION}" -o /publish/osx-arm64 --nologo -v quiet; \
    dotnet publish src/MirbadLLM.HostAgent/MirbadLLM.HostAgent.csproj -c Release -r osx-arm64 \
        --self-contained true -p:PublishSingleFile=true -p:DebugType=none \
        -p:SourceRevisionId="${SOURCE_REVISION}" -o /publish/osx-arm64-hostd --nologo -v quiet; \
    cp /publish/osx-arm64-hostd/mirbadllm-hostd /publish/osx-arm64/; \
    printf 'revision=%s\nrid=%s\nbuilt=%s\n' "${SOURCE_REVISION}" osx-arm64 "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >/publish/osx-arm64/BUILD-INFO; \
    bash deploy/agent/macos/install-agent.sh package --published-dir /publish/osx-arm64 --rid osx-arm64 --output /bundles/osx-arm64.tar.gz; \
    rm -rf /publish/osx-arm64 /publish/osx-arm64-hostd; \
    ls -l /bundles

# Demo image (--target demo; docs/setup.md, "Demo"): the master's files and deploy/demo. demo.sh starts this master and
# two agents on the development fake runtime in the one container, with the agent binary taken from the Linux bundle
# above, adds fake models and keeps sending them requests. Fake data, no login: for a look at the UI, never a deployment.
# No RUN, so the other architecture needs no QEMU; the state lives in /tmp and goes with the container.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_TAG} AS demo
WORKDIR /app
COPY --from=build /app .
COPY --from=agent-bundles /bundles /app/agent-bundles
COPY deploy/release-notes.json /app/release-notes.json
COPY deploy/demo/ /opt/mirbadllm-demo/
EXPOSE 8080
USER app
# Healthy once the fake fleet is seeded (demo.sh writes the file); deploy/release.sh waits for it before it pushes.
HEALTHCHECK --interval=5s --timeout=3s --start-period=120s --retries=3 CMD ["test", "-f", "/tmp/mirbadllm-demo/ready"]
ENTRYPOINT ["bash", "/opt/mirbadllm-demo/demo.sh"]

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_TAG}
WORKDIR /app
COPY --from=build /app .
# Read (and hashed) once at startup by AgentBundleCatalog (MirbadLLM:NodeApi:AgentBundleDirectory overrides the folder).
COPY --from=agent-bundles /bundles /app/agent-bundles
# The release notes of this build (the deploy job writes them from the merged PRs; a local build has the empty placeholder).
COPY deploy/release-notes.json /app/release-notes.json
COPY --from=build --chown=1654:1654 --chmod=0700 /layout/bootstrap /run/mirbadllm-bootstrap
ENV ASPNETCORE_HTTP_PORTS=8080 \
    MirbadLLM__DataPath=/data
# 8080: UI, /api, /v1, /metrics. 8443: the enroll listener (TLS, design §3.2); in P1 only on the mirbadllm network.
EXPOSE 8080 8443
# Runs as the image's non-root "app" user.
USER app
ENTRYPOINT ["dotnet", "MirbadLLM.Web.dll"]
