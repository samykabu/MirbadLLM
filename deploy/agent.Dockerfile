# mirbadllm-agent image (design §2.2, §2.5; MN-041). The DGX agent container in P1: it owns the Docker socket, the
# model folders, the hostd socket and GPU telemetry; the master reaches it over mTLS on :9443.
#
# Multi-arch: the build stage runs on the build machine's own platform and publishes for the target architecture, so
#   docker build -f deploy/agent.Dockerfile .                                   (native: arm64 on the DGX)
#   docker buildx build --platform linux/amd64,linux/arm64 -f deploy/agent.Dockerfile .
# both work without QEMU (the final stage has no RUN). Base images are the official .NET images.
ARG DOTNET_TAG=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_TAG} AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/MirbadLLM.Core/MirbadLLM.Core.csproj src/MirbadLLM.Core/
COPY src/MirbadLLM.Agent.Contracts/MirbadLLM.Agent.Contracts.csproj src/MirbadLLM.Agent.Contracts/
COPY src/MirbadLLM.Pki/MirbadLLM.Pki.csproj src/MirbadLLM.Pki/
COPY src/MirbadLLM.NodeRuntime/MirbadLLM.NodeRuntime.csproj src/MirbadLLM.NodeRuntime/
COPY src/MirbadLLM.Agent/MirbadLLM.Agent.csproj src/MirbadLLM.Agent/
# TARGETARCH is set by BuildKit (amd64/arm64); without it the build machine's own architecture is used.
RUN case "${TARGETARCH:-$(uname -m)}" in \
        amd64|x86_64) echo x64 ;; \
        arm64|aarch64) echo arm64 ;; \
        *) echo "unsupported architecture ${TARGETARCH:-$(uname -m)}" >&2; exit 1 ;; \
    esac > /tmp/arch && \
    dotnet restore src/MirbadLLM.Agent/MirbadLLM.Agent.csproj -a "$(cat /tmp/arch)"
COPY src/MirbadLLM.Core/ src/MirbadLLM.Core/
COPY src/MirbadLLM.Agent.Contracts/ src/MirbadLLM.Agent.Contracts/
COPY src/MirbadLLM.Pki/ src/MirbadLLM.Pki/
COPY src/MirbadLLM.NodeRuntime/ src/MirbadLLM.NodeRuntime/
COPY src/MirbadLLM.Agent/ src/MirbadLLM.Agent/
# /layout holds the directories the final stage copies with their owner and mode (no RUN there, so cross-arch builds need
# no QEMU): the state directory (the DGX's named volume takes this owner and mode on first use) and the bootstrap
# directory shared with the master (0700, uid 1654, plan §3.6 W4 notes).
# The commit (ci-deploy-multinode.sh passes it): the agent reports 1.0.0+<commit>, as the bundles for other nodes do, and
# the portal compares it with the master's. Declared after the restore, so a new commit keeps that layer cached.
ARG SOURCE_REVISION=unknown
# The release version (deploy/release.sh): MSBuild reads it from the environment as the Version property, as in
# deploy/Dockerfile. Empty keeps 1.0.0.
ARG Version=
RUN set -eu; \
    case "${SOURCE_REVISION}" in *[!A-Za-z0-9._-]* | "") echo "SOURCE_REVISION must be letters, digits, '.', '_' or '-'" >&2; exit 1 ;; esac; \
    dotnet publish src/MirbadLLM.Agent/MirbadLLM.Agent.csproj -c Release -a "$(cat /tmp/arch)" --self-contained false \
        --no-restore -p:SourceRevisionId="${SOURCE_REVISION}" -o /app; \
    mkdir -p /layout/state /layout/bootstrap /layout/models /layout/cache

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_TAG}
WORKDIR /app
COPY --from=build /app .
# The DGX configuration (P1): container paths plus the node's static settings. Host-specific values (bind sources, image
# overrides) come from MIRBADLLM_AGENT_Agent__* environment variables set by deploy/compose.multinode.yaml.
COPY deploy/agent/agent.dgx.json /etc/mirbadllm-agent/agent.json
COPY --from=build --chown=1654:1654 --chmod=0700 /layout/state /var/lib/mirbadllm-agent
COPY --from=build --chown=1654:1654 --chmod=0700 /layout/bootstrap /run/mirbadllm-bootstrap
COPY --from=build --chown=1654:1654 --chmod=0755 /layout/models /models
COPY --from=build --chown=1654:1654 --chmod=0755 /layout/cache /cache
ENV MIRBADLLM_AGENT_CONFIG=/etc/mirbadllm-agent/agent.json \
    DOTNET_gcServer=0
EXPOSE 9443
# Non-root "app" user (uid 1654); compose adds the Docker socket's group.
USER app
ENTRYPOINT ["dotnet", "mirbadllm-agent.dll"]
