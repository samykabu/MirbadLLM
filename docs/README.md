# MirbadLLM documentation

MirbadLLM gathers the machines you already own, from an old gaming PC to a laptop, a Mac or a DGX Spark, into one
place that runs AI models and manages them. These guides cover installing it, adding machines, and running it day to
day. They are also published on the website, with search:
[mirbadllm.vektragrid.com/docs](https://mirbadllm.vektragrid.com/docs/).

Start with the [setup guide](setup.md) to install MirbadLLM, then read the [architecture](architecture.md) when you
want to know how the parts fit together. The design documents further down record why the system is built the way it
is; they are working documents written phase by phase, so they mention phases (P1 to P4), decision numbers (D-nnn)
and work items (MN-nnn) from the project's earlier development history.

## Using MirbadLLM

| Doc | What it covers |
|---|---|
| [setup.md](setup.md) | Install on one machine, add Linux, Windows and macOS nodes, run the development fleet, troubleshooting |
| [configuration.md](configuration.md) | `deploy/.env` variables, master settings, agent configuration files |
| [backup-and-restore.md](backup-and-restore.md) | What a backup holds, scheduled backups, NAS destinations, the disaster recovery runbook |
| [host-services.md](host-services.md) | The optional host agent: managing host daemons and containers, its security model |
| [../deploy/agent/linux/README.md](../deploy/agent/linux/README.md) | Linux PC nodes: install, join, firewall, upgrade, removal |
| [../deploy/agent/windows/README.md](../deploy/agent/windows/README.md) | Windows nodes: install, llama.cpp, GGUF import, upgrade, removal |
| [../deploy/agent/macos/README.md](../deploy/agent/macos/README.md) | macOS nodes: install, llama.cpp on Metal, MLX, launchd host services |

## Operator guides

| Doc | What it covers |
|---|---|
| [multinode/operations.md](multinode/operations.md) | The master and agent split: configuration, enrollment, security, certificates, backups, diagnosis |
| [multinode/operations-p2.md](multinode/operations-p2.md) | Linux PC nodes, discrete GPUs, placement, moves, per-node jobs, takeover, audit log, CA rotation |
| [multinode/operations-p3p4.md](multinode/operations-p3p4.md) | Windows nodes, native llama-server processes, GGUF models, per-node admin APIs |

## Architecture and design

| Doc | What it covers |
|---|---|
| [architecture.md](architecture.md) | Components, request flow, project ownership, conventions and behaviour contracts |
| [multinode/design.md](multinode/design.md) | Multi-node architecture: invariants, protocol, decisions log |
| [multinode/design-p3p4.md](multinode/design-p3p4.md) | The process runtime (llama.cpp) and the Windows service |
| [multinode/agent-updates.md](multinode/agent-updates.md) | Signed agent updates, release notes, rollback |
| [macos/implementation-plan.md](macos/implementation-plan.md) | macOS nodes: llama.cpp on Metal and MLX, phase by phase |
| [tuning/implemented.md](tuning/implemented.md) | Where each part of the tuner lives in the code |
| [../DESIGN.md](../DESIGN.md) | The UI design system: tokens, typography, components |
| [../PRODUCT.md](../PRODUCT.md) | Product brief: users, purpose, principles |

## Development

| Doc | What it covers |
|---|---|
| [../CONTRIBUTING.md](../CONTRIBUTING.md) | Development setup, build and test commands, pull requests, CI |
| [multinode/dev-setup-windows.md](multinode/dev-setup-windows.md) | A Windows box running the master plus Linux and Windows dev agents, without GPUs or Docker |

## Images

Diagrams and screenshots live in [images/](images/):

| Image | Shows |
|---|---|
| [images/architecture.png](images/architecture.png) | System architecture: clients, master, agents, runtimes |
| [images/request-flow.png](images/request-flow.png) | A `/v1` request from the client to the runtime and back |
| [images/deployment.png](images/deployment.png) | Single-machine and multi-node deployments |
| [images/repo-structure.png](images/repo-structure.png) | The projects in `src/` and how they depend on each other |
| [images/screenshots/](images/screenshots/) | UI screenshots from the development fleet |

Each diagram also has an SVG version next to the PNG.
