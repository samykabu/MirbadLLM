# Changelog

Release notes are collected from merged PRs by `deploy/release-notes.py` and shown in the UI under
**Releases**. This file is a static snapshot for reference. Pull request numbers refer to the project's earlier
private development history.

## Unreleased

- A busy SGLang model no longer reads as Starting, which made the router refuse its requests while a long prompt was processing. It takes one restart to apply
- The Add model wizard moves keyboard focus to the step it opens, password managers leave the registry login alone, and the Overview and Tune help no longer describe settings a runtime doesn't have
- Tuning works on MLX (parallel requests, KV bits, draft tokens, prompt cache) and SGLang (max running requests, schedule policy, draft depth); NIM stays benchmark-only because it has nothing to tune
- Benchmarks size their load and prompts for each runtime, report speculative acceptance where the server does, count tokens on servers that report no usage, and no longer claim "as requested" on runtimes whose logs they can't read
- NIM: secrets such as `NGC_API_KEY` come from saved tokens, private images can be pulled with a registry login, the served name is enforced, and no checkpoint folder is needed in the wizard, moves, restores or MCP
- SGLang and NIM models can set environment variables; MCP can't change the image or arguments of a model that has secrets
- SGLang and NIM report live numbers: tokens per second, running and waiting requests, KV usage, latencies, and stall and slow warnings. A SGLang model needs one restart to start reporting them, and shows Restart pending until then
- The model page says why an MLX model has no live numbers
- A fleet of one node with discrete GPUs can now choose GPUs in the model editor; before, every save was refused for having none
- SGLang and NIM: the Add model wizard no longer shows an empty Tuning step or a budget rule for a field they don't have, and changing the node drops a runtime the new node can't run
- SGLang: several selected GPUs now set `--tp-size`; `--context-length` and `--tool-call-parser` in Extra args reach the context figures, tags and `/v1/models`; the image field suggests images the node has
- A model whose draft model folder has no source can't be moved, because it would arrive without the drafter
- Overview and the Models panel show the container image, and each runtime's own context length
- MCP: `mirbad_update_model` changes a model's settings without restarting it, and `mirbad_get_model_logs` reads its server's latest log lines with an MCP Operator key
- MCP: `mirbad_deploy_model` accepts `extraArgs`, stored as the container, llama.cpp, MLX or vLLM extra arguments
- Moving a model pulls the image its runtime actually uses, and a native-process model no longer waits on a node without Docker
- SGLang and NIM settings are checked on every save, and a flag with its value on one line of Extra args now works
- `PUT /api/models` keeps `mlx`, `container` and `speculative` when the body leaves them out
- Tuning is refused on MLX, SGLang and NIM with a clear reason, because the search couldn't change anything on them; benchmarks still work
- Ready MLX, SGLang and NIM models show on the Overview leaderboard and in the memory charts; the Apply dialog and saved-settings prompts list real changes for SGLang, NIM, llama.cpp and MLX
- SGLang speculative decoding: set the algorithm, draft depth and draft model in the model editor or through `mirbad_deploy_model`
- MCP: `mirbad_deploy_model` selects the runtime, GPUs, image and memory budget, and sets the KV cache type, prefix caching and speculative decoding with a draft model; `mirbad_list_node_runtimes` lists what each node supports. An MCP Operator key can choose any container image, within each node's digest policy. MCP clients are told on connect, in the tool descriptions and in the deploy answer what each runtime supports and what a deploy doesn't prove
- Access > MCP: a new tab lists every MCP tool, gives setup steps for Codex, Claude Code and Claude Desktop, and has a prompt that asks an agent to configure itself; the Remote MCP panel moved here from Connect
- Fix MCP tool output schemas so Claude Code can load the tools; `structuredContent` now holds the result directly instead of under a `result` property
- MCP health and download tools report a node that can't be reached instead of failing, and fleet health counts downloads on every node; the downloads API answers 503, not 500, for a node with no agent connection
- Access > API keys: **Add key** opens the create form in a dialog that also reveals the new key once; the keys table spans the page, and the VS Code block (also on Connect) is gone
- Removed the fleet level, XP and quests from the Overview; the fleet counters and service levels stay
- Rename SparkServe to MirbadLLM everywhere; a deployment, its backups and its agents from before the rename upgrade in place (docs/setup.md, "Upgrading from a pre-rename install")
- Overview "Command Deck": Overview and Monitoring merged into one page with a node scope rail, a WebGL Signal chart, fleet counters, service levels, a leaderboard and an inline readout; `/monitoring` redirects to it
- Rebrand LLMesh as MirbadLLM: cube mark, graphite-and-amber light and dark themes, Plus Jakarta Sans and Inter
- Saved tokens under Access > Tokens, picked by name for downloads, moves and restores, and carried in encrypted backups
- Fix the squeezed "Create a key" card; versioned favicon and a /favicon.ico with the new mark
- Back up and restore everything: running models, node settings, managed services, history, agent updates, and the backup share with its password (encrypted)

## 2026-10-23

- Explain degraded node health after agent build verification (#175)
- Fix degraded-node agent updates and use portal sessions for admin actions (#174)

## 2026-10-22

- Add automatic model recovery and queued starts; refine Access and node settings (#173)
- Fix Monitoring deep links and remove redundant UI prompts (#172)

## 2026-10-20

- Contain long model names and agent update hashes (#171)
- Keep fleet status warnings inside narrow screens (#170)
- Restore UI v2 fidelity to the OpenDesign reference (#169)
- Redesign the LLMesh console with the UI v2 rail and page layouts (#168)

## 2026-10-18

- Monitoring v2: fleet pulse, node rail and model table, readout in a side panel (#167)
- Flag stalled and slow models; end proxied requests a silent model never answers (#166)

## 2026-10-17

- Nodes page: the automatic agent updates switch is always shown to admins (#165)
- Process driver: a llama-server whose GPU backend failed at warm-up is Failed with the cause, not Ready (#164)
- Backup round trip keeps an MLX model's settings and its Mac placement (#163)
- Adopted MLX instances: derive the readiness alias from the argv rules, without the executable check (#162)
- macOS Phase 5: fixes from the end-to-end checks, docs (#161)
- install-agent.ps1: set a service's program through WMI, not sc.exe binPath= (#160)
- Push agent updates from the master (Linux, macOS, Windows), with Update all and automatic updates (#159)
- Release notes from the merged PRs: Releases page, What's new, agent-update notes (#158)
- Show controller and agent versions; flag agents from another build (#157)
- install-hostd: read agent.json keys back with plutil raw (#156)
- macOS Phase 4: Apple GPU telemetry and launchd host services (#155)
- Re-read a node's capabilities every 60 s while its stream is up (#154)
- macOS Phase 3: the MLX runtime (#153)
- macOS Phase 2: llama.cpp on Metal (#152)
- Let the macOS packaging files into the image's build context (#151)
- Build images from pinned copies of the .NET base images (#150)

## 2026-10-16

- macOS Phase 1: a Mac joins the fleet (#149)
- macOS Phase 0: Mac CI leg, TLS credentials on macOS, spike results (#147)
- Copy to clipboard works over plain HTTP (#148)
- Add model wizard: steps open strictly in order, Next validates its step (#146)
- Add model becomes a wizard; Backup page regrouped by job (#145)
- Rebrand the portal as LLMesh with the Geodesic Silver design system (#144)
- Portal redesign: developer-console world, job-based navigation, model workspace, guided tour (#143)

## 2026-10-10

- Multi-node P3: llama.cpp as a native process runtime (#141)
