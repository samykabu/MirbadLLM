# Changelog

Release notes are collected from merged PRs by `deploy/release-notes.py` and shown in the UI under
**Releases**. This file is a static snapshot for reference. Pull request numbers refer to the project's earlier
private development history.

## Unreleased

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
