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

Copy `deploy/.env.example` to `deploy/.env` and adjust it. An installation from the published images keeps the same
settings in `.env` next to `compose.release.yaml`, starting from the release's `env.example`. The compose files read
these variables:

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
| `MIRBADLLM_VERSION` | `latest` | Release of the published images that `compose.release.yaml` runs: `1.2.0`, or `1.2` for the newest 1.2.x. The compose files that build ignore it |

## Master settings

The settings below are the ones operators change most. Each class in the code documents the rest next to its
property.

| Key | Default | What it does |
|---|---|---|
| `MirbadLLM:VllmImage` | a locally built image name | Default vLLM image (see `MIRBADLLM_VLLM_IMAGE`) |
| `MirbadLLM:MemoryReserveGiB` | `8` | Seed for each node's memory reserve on first boot |
| `MirbadLLM:AdminPassword` | empty | Admin login password |
| `MirbadLLM:RequireApiKey` | `false` | Require an API key on `/v1` from the start |
| `MirbadLLM:AutoStartOnRequest` | `false` | Start any stopped model when a request names it; the request waits until the model is ready or times out. A model's own **Start when a request names it** switch does the same for that model alone (see [Swap on demand](#swap-on-demand-per-model)) |
| `MirbadLLM:StartupTimeout` | `00:10:00` | How long a start may take before the model is marked Failed |
| `MirbadLLM:CrashRestart` | `true` | Restart a model whose process or container died on its own |
| `MirbadLLM:CrashRestartLimit` | `3` | Consecutive crashes after which automatic restarts pause |
| `MirbadLLM:MtpArchitectures` | empty | Model architectures the vLLM image can run with MTP speculative decoding; the tuner offers MTP only for these or for a saved MTP configuration |
| `MirbadLLM:Backup:DailyAt` | `03:00` | Time of the daily scheduled backup, in the container's local time (UTC unless you set `TZ`) |
| `MirbadLLM:Backup:Keep` | `14` | Number of scheduled backup files kept |
| `MirbadLLM:Backup:IncludeCa` | `true` | Put the master's CA into the encrypted section of a backup |
| `MirbadLLM:NodeApi:EnrollUrl` | the browser's host on port 8443 | Enroll listener URL written into join commands; set it when nodes reach the master by another name or port |
| `MirbadLLM:NodeApi:AgentBundleUrl` | the address the browser used | Base URL that join commands download agent bundles from |

## Swap on demand (per model)

Three settings on a model let a node that holds one model at a time serve several. Set them in the model editor under
**On demand**, with `PUT /api/models/{id}`, or with the MCP tools `mirbad_deploy_model` and `mirbad_update_model`. All
three are off by default. None is a launch argument, so a change applies at once and needs no restart.

| Setting in the editor | JSON field | Default | What it does |
|---|---|---|---|
| **Start when a request names it** | `startOnRequest` | `false` | A `/v1` request that names the model while it is Stopped or Failed starts it and waits until it is Ready or `MirbadLLM:StartupTimeout` passes. `MirbadLLM:AutoStartOnRequest` turns this on for every model |
| **Stop after idle (minutes)** | `idleStopMinutes` | not set: never | Stops the model once it has been Ready this many minutes without a request. The count starts at its last request, or at the moment it became Ready if it has had none. At least 1 |
| **May be stopped to make room** | `stopToMakeRoom` | `false` | When memory admission refuses a start on request, idle models on the same node that carry this setting are stopped, least recently used first, and the start is tried once more |

What to expect:

- The waiting request stays open while the model loads. llama.cpp and MLX suit this. vLLM, SGLang and NIM take 2–5
  minutes to start, so a client whose own timeout is shorter gives up before the answer arrives.
- A model with a request in flight, or with a request waiting for it to start, is never stopped. Neither is a model on
  a node where a benchmark or tuning job is running.
- Making room stops only as many models as the missing memory needs, and nothing when the idle models that may be
  stopped do not cover it. If the start still does not fit, the request gets 503 `model_not_ready`, as it does without
  these settings. Memory admission on the node is unchanged and decides every start.
- On a node with discrete GPUs, only models placed on the GPU that is short of memory are stopped. A refusal about
  host RAM on such a node stops nothing.
- A stopped model that a request starts stays in `/v1/models` and in Ollama's `/api/tags`, so a client with a model
  picker can choose it. A stopped model without the setting is left out, as before.
- A model stopped for being idle or to make room stays stopped after a node or master restart, like a model you
  stopped by hand. It starts again when a request names it (with start on request), when you start it, or at master
  boot if its **Start when the controller boots** switch is on.
- The idle count is kept in the master's memory. After a master restart it starts again for every running model.
- Both kinds of stop are written to the audit log as `model.stop`, with the actor `system:idle-stop` or
  `system:make-room`.

## MCP clients

The master exposes a stateless Streamable HTTP endpoint at `/mcp`. It has no environment-variable key setting: create revocable keys with the **MCP Read** or **MCP Operator** scope in **Access → API keys**. Keys without an MCP scope and browser credentials do not authenticate MCP. Requests are limited to 256 KiB and 120 per minute per server instance; tool pagination is capped at 100 items.

**Access → MCP** lists every tool and gives setup steps filled in with your address, plus a prompt that asks an agent to configure itself.

Use `bearer_token_env_var = "MIRBAD_MCP_KEY"` in Codex rather than static headers. For Claude Code, pass `--transport http` and a single-quoted `'Authorization: Bearer ${MIRBAD_MCP_KEY}'` header, so the saved configuration holds a reference to the variable, not the key. Claude Desktop connectors connect from Anthropic's cloud, so they need a public HTTPS URL and a `Bearer` value under **Request headers**, which is in beta. On a private network, use the `mcp-remote` community bridge in `claude_desktop_config.json`; that file then holds the key as plain text. Never put credentials in query strings.

MirbadLLM serves plain HTTP for trusted LAN deployment. Bearer credentials are visible to anyone able to intercept that traffic. For access beyond a trusted LAN, terminate TLS at a reverse proxy, restrict source networks, and configure clients with the resulting HTTPS `/mcp` URL.

## Ollama clients

The master answers Ollama's API on its main port, the port of the UI and `/v1` (8080 by default; Ollama's own default
is 11434), for clients that have an Ollama setting and no OpenAI one. Point the client at `http://<host>:8080`. The
**Access** page has the settings for your address under **Client settings > Ollama**. There is nothing to switch on.

A chat, generate or embedding request is translated to its `/v1` form and takes the `/v1` path through the router, so
model resolution, the `<served-name>-nothink` names, maintenance windows, start-on-request and the request statistics
behave as they do there.

| Route | What it does |
|---|---|
| `POST /api/chat` | Chat completion (`/v1/chat/completions`): messages, base64 `images`, `tools` and tool results, `think`, `format` |
| `POST /api/generate` | The prompt, with `system` when given, as a chat completion; with `raw: true` a plain completion (`/v1/completions`) |
| `POST /api/embed`, `POST /api/embeddings` | Embeddings (`/v1/embeddings`) |
| `GET /api/tags` | The models `/v1/models` lists: the Ready ones, and the stopped ones a request starts |
| `GET /api/ps` | The Ready models with their context length |
| `POST /api/show` | A model's capabilities (`completion`, `embedding`, `tools`, `vision`, `thinking`) and details |
| `GET /api/version` | MirbadLLM's version |

**API key.** The rule is the one of `/v1`: open until the first key exists (unless `MIRBADLLM_REQUIRE_API_KEY=true`),
then `Authorization: Bearer <key>` with a key that has the **Inference** scope. A client that cannot send that header
works only while no key is required. The `ollama` command line is such a client: `OLLAMA_HOST=http://<host>:8080`
points it at the master, and it has no setting for a bearer key. `ollama list`, `ollama ps`, `ollama show` and
`ollama run` were checked with version 0.40.16.

**Model names.** Clients send the served name, as on `/v1`. Ollama clients append `:latest` to a name without a tag,
so `<served-name>:latest` is accepted when no model is served under that exact name and `<served-name>` is.

**Streaming.** Answers stream as newline-delimited JSON unless the request says `"stream": false`, as with Ollama.

**Options.** `temperature`, `top_p`, `top_k`, `min_p`, `seed`, `stop`, `presence_penalty`, `frequency_penalty` and
`num_predict` (as `max_tokens`) are passed on. `think` switches thinking on or off
(`chat_template_kwargs.enable_thinking`), and `format` becomes `response_format`: JSON mode for `"json"`, a JSON
schema for a schema. Fields with no OpenAI equivalent are ignored without an error: `num_ctx` and the other load-time
options (the context length is the model's own setting in MirbadLLM), `repeat_penalty`, `keep_alive`, `truncate`,
`template` and `context`.

**Numbers.** `prompt_eval_count` and `eval_count` are the token counts the runtime reports. `total_duration` is
measured on the master, from the request's arrival to the end of the answer. `prompt_eval_duration` and
`eval_duration` are present only when the runtime's answer carries them (llama.cpp's `timings`); otherwise they are
left out, and `load_duration` always is. In `/api/ps` `expires_at` is a date far in the future, which `ollama ps`
shows as "Forever": Ollama's keep-alive unloads nothing here. The registry stores no file size, digest or modification time, so in
`/api/tags` `size` is 0 and `modified_at` is the zero time. `digest` is the SHA-256 of the served name, a stand-in:
`ollama list` shortens the digest to 12 characters and fails on an empty one. `details` carries only what the
registry holds: `gguf` as the format of a llama.cpp model, and the quantization when the model definition sets one.

**Not offered.**

- `/api/pull`, `/api/push`, `/api/create`, `/api/copy` and `/api/delete` answer 501 with an error that points to the
  **Models** page. Models are added and removed there, or through the admin API.
- A request to unload a model (`keep_alive: 0` with an empty prompt) is refused. Stop the model on **Models**.
- A load request (an empty prompt or message list) is answered for a model that is Ready. It does not start a stopped
  model. With `MirbadLLM:AutoStartOnRequest`, the first chat or generate request starts it.
- `suffix` (fill-in-the-middle) on `/api/generate` is refused with 400.
- The root path serves the UI, so a client that checks it for Ollama's "Ollama is running" text does not find it,
  and nothing listens on port 11434. A client that needs either one needs a reverse proxy in front. `HEAD /` is
  answered with 200 and no key, because the `ollama` command line sends it before every command.

Errors use Ollama's shape, `{"error": "<message>"}`, with the status code `/v1` would have answered.

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
