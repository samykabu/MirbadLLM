#!/usr/bin/env bash
# Builds the release images for linux/amd64 and linux/arm64 from the checked-out commit and, with PUSH=1, pushes them
# (CONTRIBUTING.md, "Cutting a release"). .github/workflows/release.yml runs it on the self-hosted Linux runner: with
# PUSH=1 for a v<version> tag, without for a manual run, which is the dry run of a release.
#
#   VERSION=1.2.0 bash deploy/release.sh                    # build both platforms and test the demo image; push nothing
#   VERSION=1.2.0 PUSH=1 bash deploy/release.sh             # the same, then push (log in to the registry first)
#   DRY_RUN=1 VERSION=1.2.0 PUSH=1 bash deploy/release.sh   # print the commands, run nothing
#
# Three images under IMAGE_PREFIX (default ghcr.io/samykabu): mirbadllm (the master, deploy/Dockerfile), mirbadllm-agent
# (deploy/agent.Dockerfile) and mirbadllm-demo (deploy/Dockerfile, target demo). Each is tagged <version>; a release that
# is not a prerelease (no "-" in the version) also moves <major>.<minor> and latest.
#
# The version goes into the binaries as <version>+<commit> (build arguments Version and SOURCE_REVISION). The commit
# stays the build's identity: the master compares it with each agent's and names the agent bundles after it, as on every
# other build.
#
# Order: build everything without pushing, start the demo image of this machine's architecture and wait until it has
# seeded its fleet and reports the version, and only then push. A push repeats the builds from the cache.
#
# Needs Docker with the buildx plugin. Both Dockerfiles compile on the build machine's platform and their final stages
# have no RUN, so the other architecture needs no QEMU. The builds run in a BuildKit container (builder
# "mirbadllm-release", created on the first run and kept for its cache) with a memory limit, because the runner shares
# the machine's memory with the models: a runaway build is killed inside the builder instead.
set -euo pipefail

VERSION="${VERSION:?set VERSION to the release version, the tag without its v (for example 1.2.0)}"
PUSH="${PUSH:-0}"
DRY_RUN="${DRY_RUN:-0}"
# Lower case: a registry path has no capitals, and the workflow passes the repository owner as GitHub spells it.
IMAGE_PREFIX="${IMAGE_PREFIX:-ghcr.io/samykabu}"
IMAGE_PREFIX="$(printf '%s' "$IMAGE_PREFIX" | tr '[:upper:]' '[:lower:]')"
SOURCE_URL="${SOURCE_URL:-https://github.com/samykabu/MirbadLLM}"
BUILDER="${RELEASE_BUILDER:-mirbadllm-release}"
BUILDER_MEMORY="${RELEASE_BUILDER_MEMORY:-16g}"
PLATFORMS="linux/amd64,linux/arm64"

if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]]; then
  echo "::error::VERSION '$VERSION' is not <major>.<minor>.<patch> or <major>.<minor>.<patch>-<prerelease>." >&2
  exit 1
fi
sha="${GITHUB_SHA:-$(git rev-parse HEAD)}"
revision="${sha:0:12}"
tags=("$VERSION")
if [[ "$VERSION" != *-* ]]; then tags+=("${VERSION%.*}" latest); fi

step() { echo "==> $*"; }
# Runs a command, or prints it in dry-run mode.
run() {
  if [ "$DRY_RUN" = 1 ]; then
    printf '+'; printf ' %q' "$@"; printf '\n'
  else
    "$@"
  fi
}

# build <image name> <output flag or ""> <tags...> -- <docker buildx build arguments>
build() {
  local name="$1" output="$2" args=(); shift 2
  while [ "$1" != -- ]; do args+=(-t "$IMAGE_PREFIX/$name:$1"); shift; done; shift
  # ${args[@]+...}: before bash 4.4 an empty array counts as unset under "set -u" (macOS has bash 3.2).
  run docker buildx build --builder "$BUILDER" \
    --build-arg "SOURCE_REVISION=$revision" --build-arg "Version=$VERSION" \
    --label "org.opencontainers.image.source=$SOURCE_URL" --label "org.opencontainers.image.version=$VERSION" \
    --label "org.opencontainers.image.revision=$sha" ${args[@]+"${args[@]}"} $output "$@" .
}

build_all() { # <output flag or "">
  build mirbadllm "$1" "${tags[@]}" -- --platform "$PLATFORMS" -f deploy/Dockerfile
  build mirbadllm-agent "$1" "${tags[@]}" -- --platform "$PLATFORMS" -f deploy/agent.Dockerfile
  build mirbadllm-demo "$1" "${tags[@]}" -- --platform "$PLATFORMS" -f deploy/Dockerfile --target demo
}

step "Release $VERSION from $revision: tags ${tags[*]} under $IMAGE_PREFIX"
if [ "$DRY_RUN" = 1 ]; then
  step "DRY_RUN=1: printing the steps only"
  echo "+ create the BuildKit builder $BUILDER (memory $BUILDER_MEMORY) unless it exists"
elif ! docker buildx inspect "$BUILDER" >/dev/null 2>&1; then
  step "Creating the BuildKit builder $BUILDER (memory $BUILDER_MEMORY)"
  docker buildx create --name "$BUILDER" --driver docker-container \
    --driver-opt "memory=$BUILDER_MEMORY" --driver-opt "memory-swap=$BUILDER_MEMORY" >/dev/null
fi

step "Building the master, agent and demo images for $PLATFORMS"
build_all ""

step "Testing the demo image on this machine"
smoke="mirbadllm-demo-smoke:$revision"
if [ "$DRY_RUN" = 1 ]; then
  echo "+ load the demo image of this machine's architecture as $smoke, run it with no published port and wait up to"
  echo "+ 180 s until it is healthy (fleet seeded) and its log names MirbadLLM $VERSION+$revision; then remove it"
else
  build mirbadllm-demo --load -- --platform "linux/$(docker version --format '{{.Server.Arch}}')" -f deploy/Dockerfile --target demo -t "$smoke"
  container="$(docker run -d --memory=4g "$smoke")"
  cleanup() { docker rm -f "$container" >/dev/null 2>&1 || true; docker rmi -f "$smoke" >/dev/null 2>&1 || true; }
  trap cleanup EXIT
  health=starting
  for _ in $(seq 1 90); do
    health="$(docker inspect "$container" --format '{{.State.Status}}/{{.State.Health.Status}}')"
    case "$health" in running/healthy | exited/* | dead/*) break ;; esac
    sleep 2
  done
  if [ "$health" != running/healthy ] || ! docker logs "$container" 2>&1 | grep -qF "MirbadLLM $VERSION+$revision"; then
    echo "::error::The demo image did not come up as MirbadLLM $VERSION+$revision ($health). Its log:"
    docker logs --tail 80 "$container" 2>&1 || true
    exit 1
  fi
  step "The demo image is healthy and reports $VERSION+$revision"
  cleanup
  trap - EXIT
fi

if [ "$PUSH" = 1 ]; then
  step "Pushing"
  build_all --push
  step "Done: pushed ${tags[*]} of $IMAGE_PREFIX/mirbadllm, mirbadllm-agent and mirbadllm-demo"
else
  step "Done: built, nothing pushed (PUSH=1 pushes)"
fi
