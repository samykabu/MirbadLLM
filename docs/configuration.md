# Configuration

MirbadLLM reads standard ASP.NET Core configuration: `appsettings.json`, then environment variables, then command-line
arguments. The master's settings live in the `MirbadLLM` section. The same key has three spellings:

| Where | Spelling | Example |
|---|---|---|
| `appsettings.json` | nested JSON | `{ "MirbadLLM": { "Backup": { "Keep": 30 } } }` |
| Environment variable | `__` between levels | `MirbadLLM__Backup__Keep=30` |
| Documentation | `:` between levels | `MirbadLLM:Backup:Keep` |

The container deployment sets the environment variables from `deploy/.env` through the compose files, so most
operators only edit that file.

## `deploy/.env`

Copy `deploy/.env.example` to `deploy/.env` and adjust it. The compose files read these variables:

| Variable | Default | What it does |
|---|---|---|
| `DOCKER_GID` | none, required | Group id of `/var/run/docker.sock` (`stat -c %g /var/run/docker.sock`); the agent container joins this group to start model containers |
| `MIRBADLLM_PORT` | `8080` | Host port of the UI, `/api`, `/v1`, `/metrics`, `/mcp` and `/healthz` |
| `MIRBADLLM_ENROLL_PORT` | `8443` | Host port of the enroll listener that joining nodes use (multi-node compose file) |
| `MIRBADLLM_DATA` | `/srv/mirbadllm/data` | Master state: the registry, API keys, nodes, the CA, history. Owned by uid 1654 |
| `MIRBADLLM_MODELS` | `/srv/mirbadllm/models` | Checkpoints root, mounted at `/models` in the agent and the model containers |
| `MIRBADLLM_CACHE` | `/srv/mirbadllm/cache` | Download, compile and autotune caches |
| `MIRBADLLM_BACKUPS` | `/srv/mirbadllm/backups` | Scheduled backups, mounted at `/backups`. Owned by uid 1654 |
| `MIRBADLLM_MEMORY_RESERVE_GIB` | `8` | Memory kept free for the operating system and other workloads when admitting a model |
| `MIRBADLLM_VLLM_IMAGE` | a locally built image name | vLLM image for models without their own image; the agent's Hugging Face download helper runs in it too. Set it to an image the machine has |
| `MIRBADLLM_API_KEY` | empty | Optional static bearer key for `/v1`. Prefer keys created on the **Access** page |
| `MIRBADLLM_REQUIRE_API_KEY` | `false` | `true` = `/v1` requires a key even before the first key exists |
| `MIRBADLLM_ADMIN_PASSWORD` | empty | Admin password for the UI and `/api`. Empty disables the login, and every page shows a warning |
| `MIRBADLLM_BACKUP_PASSPHRASE` | empty | Encrypts the secrets section of scheduled backups. Empty = scheduled backups carry no secrets. Keep a copy elsewhere: a restore needs it |
| `MIRBADLLM_BACKUP_HOSTNAME` | `dgx-spark` | Host name written into backup file names |

## Master settings

The settings below are the ones operators change most. Each class in the code documents the rest next to its
property.

| Key | Default | What it does |
|---|---|---|
| `MirbadLLM:VllmImage` | a locally built image name | Default vLLM image (see `MIRBADLLM_VLLM_IMAGE`) |
| `MirbadLLM:MemoryReserveGiB` | `8` | Seed for each node's memory reserve on first boot |
| `MirbadLLM:AdminPassword` | empty | Admin login password |
| `MirbadLLM:RequireApiKey` | `false` | Require an API key on `/v1` from the start |
| `MirbadLLM:AutoStartOnRequest` | `false` | Start a stopped model when a request names it; the request waits until the model is ready or times out |
| `MirbadLLM:StartupTimeout` | `00:10:00` | How long a start may take before the model is marked Failed |
| `MirbadLLM:CrashRestart` | `true` | Restart a model whose process or container died on its own |
| `MirbadLLM:CrashRestartLimit` | `3` | Consecutive crashes after which automatic restarts pause |
| `MirbadLLM:MtpArchitectures` | empty | Model architectures the vLLM image can run with MTP speculative decoding; the tuner offers MTP only for these or for a saved MTP configuration |
| `MirbadLLM:Backup:DailyAt` | `03:00` | Time of the daily scheduled backup, in the container's local time (UTC unless you set `TZ`) |
| `MirbadLLM:Backup:Keep` | `14` | Number of scheduled backup files kept |
| `MirbadLLM:Backup:IncludeCa` | `true` | Put the master's CA into the encrypted section of a backup |
| `MirbadLLM:NodeApi:EnrollUrl` | the browser's host on port 8443 | Enroll listener URL written into join commands; set it when nodes reach the master by another name or port |
| `MirbadLLM:NodeApi:AgentBundleUrl` | the address the browser used | Base URL that join commands download agent bundles from |

## MCP clients

The master exposes a stateless Streamable HTTP endpoint at `/mcp`. It has no environment-variable key setting: create revocable keys with the **MCP Read** or **MCP Operator** scope in **Access → API keys**. Keys without an MCP scope and browser credentials do not authenticate MCP. Requests are limited to 256 KiB and 120 per minute per server instance; tool pagination is capped at 100 items.

**Access → MCP** lists every tool and gives setup steps filled in with your address, plus a prompt that asks an agent to configure itself.

Use `bearer_token_env_var = "MIRBAD_MCP_KEY"` in Codex rather than static headers. For Claude Code, pass `--transport http` and a single-quoted `'Authorization: Bearer ${MIRBAD_MCP_KEY}'` header, so the saved configuration holds a reference to the variable, not the key. Claude Desktop connectors connect from Anthropic's cloud, so they need a public HTTPS URL and a `Bearer` value under **Request headers**, which is in beta. On a private network, use the `mcp-remote` community bridge in `claude_desktop_config.json`; that file then holds the key as plain text. Never put credentials in query strings.

MirbadLLM serves plain HTTP for trusted LAN deployment. Bearer credentials are visible to anyone able to intercept that traffic. For access beyond a trusted LAN, terminate TLS at a reverse proxy, restrict source networks, and configure clients with the resulting HTTPS `/mcp` URL.

## Node agent settings

Each agent reads its own `agent.json` (section `Agent`), written by the installer. The examples in
`deploy/agent/` show every field the installers set:

- `deploy/agent/agent.dgx.json`: the agent container on a DGX Spark-style unified-memory host
- `deploy/agent/agent.linux.example.json`: a Linux PC with discrete NVIDIA GPUs (systemd service)
- `deploy/agent/agent.windows.example.json`: a Windows PC (Windows service, llama.cpp processes)
- `deploy/agent/agent.macos.example.json`: an Apple Silicon Mac (LaunchDaemon, llama.cpp and MLX)

In the agent container, environment variables with the prefix `MIRBADLLM_AGENT_` override `agent.json`, for example
`MIRBADLLM_AGENT_Agent__MemoryReserveGiB=8`. The operator guides explain the node settings that the master manages
from the node's page, such as the memory reserve and approved images. `Agent:EnableNim=true` opts a Linux/DGX agent
into NIM capability reporting only when `ApprovedImages` also contains at least one pinned repository digest; NIM
launches always require an explicit model image and matching approved digest.

## Development settings

`src/MirbadLLM.Web/appsettings.Development.json` puts the master's data in `./.data` and points the enroll listener and
the bootstrap agent at loopback. Two switches exist only for development, and both refuse to start outside the
`Development` environment: the agent's `DevFakeRuntime` (fake models, no Docker or GPU) and the master's
`MirbadLLM:DevJoinTokens`. See [setup.md](setup.md#development-fleet).
