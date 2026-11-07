# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

People who want to run LLMs on hardware they already own: an old gaming PC or gaming laptop with an NVIDIA card,
an Apple Silicon Mac or MacBook, a Linux PC, sometimes a DGX Spark, and the few technical people they share those
machines with. They know LLMs, vLLM, Docker and OpenAI-style APIs. They open the UI to put models on the box, start
and stop them, check whether a model is up and how busy the machine is, try a model in a quick chat,
and hand out API access. Other people, coding agents (Cline, Roo Code, Continue) and subagents never
see the UI; they only call the `/v1` API.

## Product Purpose

MirbadLLM reuses spare and older machines to serve small and medium local LLMs, puts them behind a single
OpenAI-compatible endpoint, and gives the operators one browser UI to manage and watch all of them. A typical master is a DGX Spark that also runs container models. Linux and DGX agents run
vLLM, llama.cpp and SGLang containers; NIM is exposed only on explicitly configured nodes with an approved image digest.
Windows and macOS agents preserve native llama.cpp, and Apple Silicon Macs also run native MLX. Windows support for
SGLang or NIM means enrolling a separate Linux agent inside WSL2; a native Windows agent never controls that Docker runtime.
The master's router sends each OpenAI-compatible request to the node that serves the model. It exists because a
single-slot server (Ollama at the time) made parallel agents queue behind each other. Success: models are easy to add and run, many clients are served at once, and the operator can
see at a glance what is running, how fast, and how much of the shared machine it uses.

## Positioning

A purpose-built control panel for a handful of shared, memory-constrained machines: the DGX Spark (about
121 GB unified memory shared by CPU and GPU, also used by ComfyUI) and discrete-GPU worker PCs. Memory
admission per node and per GPU before a model starts, live throughput and latency, fixed placement of each
model on a node, and one endpoint for every model. It is not a general cluster scheduler and not a hosted
service.

## Operating Context

- Used mostly in a desktop browser next to VS Code while working, and on a phone or tablet for quick
  checks (is the model up, memory, start/stop).
- LAN only by default, plain HTTP on port 8080; a TLS reverse proxy goes in front for anything wider.
- Starting a model takes 2–5 minutes (weights load plus GPU kernel compilation); the UI must make that
  wait legible.
- Memory is the scarce resource; other workloads on each machine change what is free.
- Typical fleet is small: two or three nodes and a handful of models (chat, embedding, intent planner).

## Capabilities and Constraints

- Pages: Overview (the live "Command Deck"; it took over Monitoring), Models, Add/Edit model, Model logs, Benchmark, Tune, Chat, Nodes, Node
  detail, Services, Connect, API keys, Backup & restore, Login.
- Blazor Web App (ASP.NET Core, .NET 10), interactive server rendering, Bootstrap 5 as the base
  component library. No JavaScript frameworks or chart libraries; charts are inline SVG, except the Overview's Signal
  chart, a small plain-WebGL module (`wwwroot/js/overview.js`) with a Canvas2D fallback and a text table.
- Data comes from each node's agent (Docker or the process host, `/proc/meminfo`, Windows or macOS memory APIs,
  `nvidia-smi` or IOKit for an Apple GPU, and vLLM's or llama.cpp's Prometheus metrics), sampled every 5 s and kept for one hour in
  memory.
- Terminology: *model* (a registered definition), *served name* (what API clients send as `model`),
  *budget* (memory reserved for a model), *node* (a machine in the fleet), *runtime* (vLLM, llama.cpp, MLX, SGLang or NIM),
  *placement* (the node and GPUs a model runs on), states *Stopped, Starting, Ready, Stopping, Failed*.
- Access: admin login for the UI; API keys for `/v1` and `/metrics`.

## Brand Commitments

- Tone: calm and technical. Neutral surfaces in light and dark, colour only for
  status and the primary action, tabular monospace numerals for measurements.
- Craft bar (products it should sit alongside): Vercel / Linear, Grafana, Portainer / Proxmox.
- Both light and dark themes are required.
- Name and identity (2026-10-16, renamed 2026-11-01): the product is **MirbadLLM** (from مِرْبَد, the Arabic word for
  the enclosure where camels are gathered and kept), with the graphite-and-amber brand: the isometric cube
  "M" mark, Plus Jakarta Sans for the wordmark and headings, Inter for the interface, Azeret Mono for measurements.
  It replaced an earlier working name that was too close to the unrelated Mesh LLM project and to several existing
  packages. The mark is always shown exactly as delivered (transparent, never filtered, recoloured or backed).

## Evidence on Hand

- The Benchmark and Tune pages measure each model on its own node; results are stored per model.
- MirbadLLM cube mark, favicon and touch icon in `src/MirbadLLM.Web/wwwroot/brand/` (transparent PNGs), cut from the delivered sources in `docs/brand/`.

## Product Principles

1. State first: what is running, what is failing and what memory is left must be readable in seconds.
2. Honest numbers: show measured values or "—", never invented or smoothed-over data.
3. Safe by default on a shared box: refuse what does not fit, explain why, allow an explicit override.
4. Guide the expert: sensible defaults and explanations without hiding the real vLLM settings.

## Accessibility & Inclusion

WCAG 2.2 AA in both light and dark themes: text and UI contrast, full keyboard use, visible focus,
status never conveyed by colour alone, usable at phone widths.
