# MirbadLLM multi-node: Operator guide (P1)

How the Phase 1 (P1) multi-node build is put together, configured, enrolled, secured, backed up and diagnosed. Every
statement here is checked against the code on the P1 branch; where the [design](design.md) describes something P1 does
not ship yet, this guide says so.

**Status:** P1 is **live** on the DGX: `main` deploys `deploy/compose.multinode.yaml` with
`deploy/ci-deploy-multinode.sh`. P2 (Linux PC nodes, discrete GPUs, placement, moves, per-node jobs, takeover, restore
modes, audit log, CA rotation) is developed on `feature/multinode-p2` and not deployed; it has its own guide,
**[operations-p2.md](operations-p2.md)**, which also holds the P2 deployment checklist. Where this guide says "P2", the
P2 guide describes what the code now does. Read the plan for the development/deployment boundary.

---

## 1. Architecture overview

mirbadllm splits into a **master** and one **agent per node**. All clients talk to the master; the master never
touches a host directly (no Docker socket, no `/models`, no hostd socket, no GPU devices). On the DGX the master and its
agent run as two containers from one compose file (`deploy/compose.multinode.yaml`).

```
Clients (mirbad.example.com / 192.0.2.191, plain HTTP :8080)
         ↓
    Master (container "mirbadllm")
     ├─ Router (/v1/*)
     ├─ Admin API (/api/*)
     ├─ UI (Blazor, incl. /nodes)
     ├─ /metrics, /healthz
     └─ Enroll listener :8443 (not published in P1)
         ↓ mTLS, HTTP/2, master dials the agent
    Agent on the DGX (container "mirbadllm-agent", :9443)
     ├─ Docker Engine API (model containers ss-<id>)
     ├─ mirbadllm-hostd socket (Services page, backup mount)
     └─ nvidia-smi (telemetry only)
```

### Key components

| Component | Runs | Purpose |
|-----------|------|---------|
| **Master** | container on the DGX (`mirbadllm`) | UI, registry, API keys, tags, backups, `/v1`, placement, monitoring, jobs, PKI (CA), fencing |
| **Agent** | container on the DGX (`mirbadllm-agent`); systemd service on Linux PCs (P2), Windows service (P4) | Local executor on a node: Docker, model containers, downloads, hostd, telemetry, reverse proxy to the models |
| **hostd** | systemd service on the DGX, unchanged (`deploy/hostd`) | Root helper for systemd units, GPU-process lookup and the backup-share mount; the agent forwards to its socket |

### Data flow

- **Client request:** `Client → master /v1/... → agent https://{agent}/agent/v1/proxy/{modelId}/v1/... → model
  container (http://ss-{id}:8000)`. The router resolves the served name from the registry's committed placement and the
  node status cache; it never calls an agent per request to look something up.
- **Master to agent:** HTTPS with mutual TLS over HTTP/2. Every mutating request carries the master's fencing triple
  in `MirbadLLM-Master: {instanceId}/{epoch}/{bootId}` and a `MirbadLLM-Request-Time` (the agent refuses more than
  5 minutes of skew with `clock_skew`).
- **Agent to master:** the agent never opens a connection. The master keeps one event stream per node open
  (`GET /agent/v1/events`, SSE), pulls telemetry (`GET /agent/v1/telemetry?bootId=&afterSeq=`) and takes a full
  `GET /agent/v1/status` snapshot on every (re)connect.

### Network

| Endpoint | Where | Default |
|----------|-------|---------|
| Master application (UI, `/api`, `/v1`, `/metrics`, `/healthz`) | published on the DGX | `:8080` (`ASPNETCORE_HTTP_PORTS=8080`, `MIRBADLLM_PORT`) |
| Enroll listener (`POST /enroll`, TLS) | master only; `expose` only in P1, not published | `:8443` (`MirbadLLM:Enrollment:Port`) |
| Agent (mTLS, HTTP/2) | `https://mirbadllm-agent:9443` on the `mirbadllm` Docker network, not published | `:9443` (`Agent:Port`) |

---

## 2. Configuration

### Master

The master is configured through the `MirbadLLM` configuration section, set as environment variables in
`deploy/compose.multinode.yaml` from `deploy/.env` (the operator's env file; `ci-deploy-multinode.sh` reads
`$HOME/mirbadllm/repo/deploy/.env` unless `MIRBADLLM_ENV_FILE` says otherwise). Colons become double underscores:
`MirbadLLM:Enrollment:Port` is `MirbadLLM__Enrollment__Port`.

#### Multi-node options

| Key | Type | Default | Purpose |
|-----|------|---------|---------|
| `MirbadLLM:DataPath` | path | `/data` | State directory (see "Files in `/data`" below) |
| `MirbadLLM:MemoryReserveGiB` | double | 8 | Seeds the bootstrap node record's memory reserve on first boot (today's global reserve); after that the node record holds it |
| `MirbadLLM:Nodes:BootstrapNodeId` | string | `dgx` | The node id of the agent on the master's host; migrated and seeded models are placed on it |
| `MirbadLLM:Enrollment:Port` | int | 8443 | Enroll listener port; `≤ 0` disables the listener |
| `MirbadLLM:Enrollment:ListenerAddresses` | string[] | `mirbadllm`, `localhost` | DNS names / IPs in the enroll-listener certificate (informational: the agent checks the CA hash and the SPIFFE id) |
| `MirbadLLM:Enrollment:BootstrapEnabled` | bool | true | Write the bootstrap bundle while the bootstrap node is not enrolled |
| `MirbadLLM:Enrollment:BootstrapDirectory` | path | `/run/mirbadllm-bootstrap` | Where the bundle (`bootstrap.json`) is written |
| `MirbadLLM:Enrollment:BootstrapEnrollEndpoint` | URL | `https://mirbadllm:8443` | The enroll address written into the bundle (how the agent reaches the master) |
| `MirbadLLM:Enrollment:BootstrapAgentAddress` | URL | null = `https://mirbadllm-agent:9443` | The agent address the master records for the bootstrap node (how the master reaches the agent) |
| `MirbadLLM:Enrollment:BootstrapTokenLifetime` | TimeSpan | 24 h | Lifetime of the bundle's single-use token; the bundle is renewed 1 h before expiry, checked every 5 min |
| `MirbadLLM:Backup:IncludeCa` | bool | true | Put the CA (certificate and key) into the encrypted section of backups; only ever with a passphrase |
| `MirbadLLM:Fencing:Takeover` | bool | false | Request a fencing takeover (see section 8); one run only |
| `MirbadLLM:Pki:CaCertPem` / `CaKeyPem` | PEM | unset | CA from configuration, for topology (b) only (D63). The DGX keeps the CA in `{DataPath}/pki` and its compose sets neither |

Model registry, API keys, admin password, backup schedule and `VllmImage` keep their single-host keys.

#### Files in `/data`

| File | Contents |
|------|----------|
| `models.json` | Model registry, schema v2 (placement per model); `models.v1.json` is the copy of the v1 file kept by the one-time migration |
| `nodes.json` | Node registry: identity, SPKI pins (current and pending), approval, draining, reserves, certificate expiry, rotation record |
| `master.json` | Fencing identity: instance id (created once) and epoch (incremented on every boot) |
| `master.lock` | Held open exclusively while the master runs; a second master on the same volume does not start |
| `operations.json` | The master's references to agent operations (written before a mutation is sent) |
| `pki/` | `ca.crt`, `ca.key`, the master's agent-client certificate (`master-client.crt/.key`) and enroll-listener certificate (`enroll.crt/.key`) |
| `takeover` | Only when the operator creates it (section 8) |

There is no audit log in P1. P2 adds `{DataPath}/audit/`, `ownership.json` and the CA rotation files ([P2 guide §13.3](operations-p2.md#133-files)).

Since MN-047 (D69) every master store that is written by temp file + rename (`master.json`, `nodes.json`,
`models.json`, API keys, `operations.json`, the `pki/` files) also fsyncs its directory after the rename on Linux. This
is best effort: each store is fail-safe if a rename is lost. The agent fsyncs its operation log, fencing, enrollment
and rotation state the same way and treats a failure as an error.

### Agent

Configuration file: `/etc/mirbadllm-agent/agent.json` (Linux), `%ProgramData%\mirbadllm-agent\agent.json`
(Windows), or the path given with `--config <path>` or the `MIRBADLLM_AGENT_CONFIG` environment variable. On the DGX
the file is baked into the agent image from `deploy/agent/agent.dgx.json`; `deploy/agent/agent.linux.example.json` and
`agent.windows.example.json` are the P2/P4 templates.

Environment variables override the file with the prefix `MIRBADLLM_AGENT_` and the full key, for example
`MIRBADLLM_AGENT_Agent__Port=9444`. The DGX compose file sets `Agent__ModelsHostPath`, `Agent__CacheHostPath`,
`Agent__MemoryReserveGiB` and `Agent__DownloadHelperImage` this way.

#### Schema (DGX, abridged)

```json
{
  "Agent": {
    "NodeId": "dgx",
    "StateDirectory": "/var/lib/mirbadllm-agent",
    "Port": 9443,
    "ModelsRoot": "/models",
    "ModelsHostPath": "/srv/mirbadllm/models",
    "CacheRoot": "/cache",
    "CacheHostPath": "/srv/mirbadllm/cache",
    "DockerEndpoint": "unix:///var/run/docker.sock",
    "AllowTagPulls": true,
    "HostdSocket": "/run/mirbadllm-hostd/hostd.sock",
    "HostdExpected": true,
    "BootstrapDirectory": "/run/mirbadllm-bootstrap",
    "Addresses": [ "mirbadllm-agent" ],
    "ShutdownTimeout": "00:00:30"
  }
}
```

#### Fields

Only `NodeId` and `StateDirectory` are required; everything else has a default that works for the DGX container.

| Field | Type | Default | Purpose |
|-------|------|---------|---------|
| `NodeId` | string | – (required) | Node id, `^[a-z0-9][a-z0-9-]{0,39}$`; must equal the node id in the agent certificate |
| `StateDirectory` | path | – (required) | `agent.key`, `agent.crt`, `ca.crt`, `node-uid`, `fencing.json`, the operation log, rotation state |
| `ListenAddress` | IP | every address | Address the mTLS listener binds |
| `Port` | int | 9443 | mTLS port |
| `MaxRequestBodyBytes` | long | 72 MiB | Listener body limit; at least the 64 MiB proxy limit |
| `ModelsRoot` / `ModelsHostPath` | path | `/models` / `/srv/mirbadllm/models` | The models volume as the agent sees it / as Docker sees it |
| `CacheRoot` / `CacheHostPath` | path | null / `/srv/mirbadllm/cache` | The cache volume as the agent sees it / as Docker sees it |
| `DockerEndpoint` | string | `unix:///var/run/docker.sock` | Docker Engine endpoint (`unix://`, `npipe://`, `tcp://`) |
| `DockerNetwork` / `UseContainerNetwork` / `UpstreamHost` | | `mirbadllm` / true / `127.0.0.1` | How the agent reaches model containers (by `ss-{id}:8000` on the network, or the loopback port) |
| `DockerStartupTimeout` | TimeSpan | 2 min | How long the agent waits for Docker at start before it reconciles anyway (launches then fail `docker_unavailable`, D56) |
| `DockerCallTimeout` | TimeSpan | 2 min | Bound of one Docker call (not pulls or followed logs); must exceed 30 s |
| `DownloadHelperImage` | string | the master's `VllmImage` default | Image of the Hugging Face download container |
| `PortRangeStart` / `PortRangeEnd` | int | 8101 / 65535 | Loopback host ports for model containers |
| `MemoryReserveGiB` | double | 8 | Reserve used when a reservation is rebuilt without its admission inputs |
| `MaxConcurrentStarts` | int? | null (unified memory: 1) | Concurrent starts on the node |
| `ApprovedImages` / `AllowTagPulls` | | empty / false | Image policy: digests that pulls and launches must match; the DGX sets `AllowTagPulls: true` because today's images are tags |
| `HostdSocket` / `HostdExpected` | | `/run/mirbadllm-hostd/hostd.sock` / false | hostd socket; when expected, a missing socket is Degraded from the first check |
| `ManageSystemdUnits` / `ManageContainers` / `ManageBackupMount` | bool | true | Which host-service features the agent offers |
| `ProxyMaxInFlight` | int | 512 | Concurrent proxied requests on the node; excess → 503 `server_busy` |
| `ProxyUpstreamIdleTimeout` | TimeSpan | 20 min | How long a model may send nothing (no response headers, or no bytes mid-stream) before the request ends: 504 `timeout` before headers, an aborted stream after. `00:00:00` turns it off |
| `BootstrapDirectory` | path | null | Bootstrap mode (DGX): enroll from the master's bundle found there; null = the agent must already be enrolled (`join`) |
| `BootstrapWait` | TimeSpan | 10 min | How long an agent without certificates waits for the bundle and an answering enroll listener, then exits non-zero (D58) |
| `Addresses` | string[] | empty | Addresses the master can reach this agent at, sent with a bootstrap enrollment |
| `ShutdownTimeout` | TimeSpan | 30 s | How long a stopping agent waits for requests and running operations; compose gives both containers `stop_grace_period: 45s` |
| `HealthCheckInterval` | TimeSpan | 5 s (the heartbeat interval) | How often the agent re-checks its health and publishes a `health` event when the Degraded reasons changed (MN-047, D68) |

#### Paths

| Purpose | DGX (container) | Linux PC (P2) | Windows PC (P4) |
|---------|-----------------|---------------|-----------------|
| Config file | `/etc/mirbadllm-agent/agent.json` (in the image) | `/etc/mirbadllm-agent/agent.json` | `%ProgramData%\mirbadllm-agent\agent.json` |
| State | named volume `mirbadllm-agent-state` at `/var/lib/mirbadllm-agent` | `/var/lib/mirbadllm-agent` | `C:\ProgramData\mirbadllm-agent\state` (example) |
| Bootstrap bundle | named volume `mirbadllm-bootstrap` at `/run/mirbadllm-bootstrap`, shared with the master only | – (`join`) | – (`join`) |
| Logs | stdout/stderr (Docker) | journald | service log |

---

## 3. Enrollment and bootstrap

### First boot on the DGX (bootstrap)

1. On start the master creates the bootstrap node record (`MirbadLLM:Nodes:BootstrapNodeId`, default `dgx`) with the
   agent address from `BootstrapAgentAddress`, pre-approved and without a pin. It starts no node-monitor worker for a
   record without a pin (D58).
2. While that node is not enrolled, the master writes `{BootstrapDirectory}/bootstrap.json` (file 0600 in a 0700
   directory, uid 1654) and keeps it valid (re-checked every 5 minutes, renewed 1 hour before the token expires).
3. The agent (in bootstrap mode) finds the bundle, connects to the enroll listener on the Docker network, checks the
   listener's certificate against the CA hash in the bundle, generates its P-256 key and CSR, and enrolls with the
   single-use token. It then deletes the bundle; the master removes a leftover one once the node is enrolled.
4. The master issues the certificate and records its pin. The node is pre-approved, so the master synchronizes it and
   it shows Online.

An agent without an identity waits up to `BootstrapWait` (10 minutes) for a bundle and an answering listener, then
exits non-zero. An agent that already has an identity retries an unreachable listener for at most 1 minute, then serves
with that identity and keeps the bundle for its next start (D58). A bootstrap token only enrolls a bootstrap node that
holds no pin, current or pending (D48), so an old bundle cannot take over an enrolled node.

### Bootstrap bundle (`bootstrap.json`)

| Field | Contents |
|-------|----------|
| `nodeId` | The node to enroll as (`dgx`) |
| `token` | Single-use join token of purpose bootstrap (secret; never printed) |
| `caCertPem` | The master CA certificate (PEM) |
| `caSpkiSha256` | Base64 SHA-256 of the CA's SPKI; the agent checks the enroll listener against it before sending the token |
| `enrollEndpoint` | `https://mirbadllm:8443` by default |
| `createdAt` | When it was written |

The bundle never holds the CA key or an agent key: the agent generates its key itself and the key never leaves it.

### `join` (P2 nodes, and the dev setup)

```bash
mirbadllm-agent join \
  --enroll-url https://<master>:8443 \
  --token <join-token> \
  --ca-hash sha256:<base64> \
  --node-id <node-id> \
  [--state-dir /var/lib/mirbadllm-agent] [--address <ip|name|https://host:port>]... [--force]
```

- The token may come from `MIRBADLLM_JOIN_TOKEN` instead of `--token` (keeps it out of the process list); it is never
  printed.
- `--force` replaces an existing identity; it deletes the previous identity's rotation state first (D48).
- The command enrolls once and exits; the agent is then started as a service.
- P1 has no way to create a join token outside development: the "Add node" dialog, the join-token API and approval are
  P2 ([P2 guide §3](operations-p2.md#3-adding-a-linux-pc)). The two-agent dev setup uses the Development-only helper
  `MirbadLLM:DevJoinTokens` (section 10).

### Losing the agent's identity

If the DGX agent's state volume is lost while the master keeps its node record, the master still holds the node's pin,
so it writes no new bundle and the bootstrap token rule refuses to re-enroll the node. P1 has no supported re-pairing
for this case (no revoke or rekey API); P2 adds rekey ([P2 guide §3.6](operations-p2.md#36-rekey-re-pair)), packaged for
Linux PCs by `install-agent.sh`. Keep the `mirbadllm-agent-state` volume.

---

## 4. Mutual TLS and certificate rotation

### Agent certificate

| Property | Value |
|----------|-------|
| Key | ECDSA P-256, generated on the agent (never leaves it) |
| Validity | 90 days |
| SAN URI | `spiffe://mirbadllm/node/{nodeId}` |
| SAN DNS/IP | none at enrollment; a rotation adds the node's addresses (validation reads the URI only, D48) |
| EKU | serverAuth + clientAuth |
| Pin | SHA-256 of the SPKI, stored in the node record on the master |

### Trust and validation

- **Agent (server):** requires a client certificate that chains to the master CA, has EKU clientAuth and the SAN URI
  `spiffe://mirbadllm/master`. Nothing else is accepted.
- **Master (client):** the agent certificate must chain to the master CA, carry EKU serverAuth and the SAN URI
  `spiffe://mirbadllm/node/{nodeId}`, and its SPKI must match the node's current pin, or the pending pin during a
  rotation. Hostname validation is replaced by the SAN and pin check.
- The master's own certificates (agent client and enroll listener) last 30 days and are re-issued at half life.

### Two-phase rotation (design §10.2, D30, D49)

The rotation is **master-driven**: the master calls the agent; the agent never calls the master. The master keeps a
rotation record in the node's entry in `nodes.json` and persists each stage **before** the step it names, so a master
crash resumes from the stored stage.

| Stage (master record) | What happens |
|---|---|
| Requested | Record written. **start:** `POST /agent/v1/pki/rotations`. The agent writes a new P-256 key, persists its stage and returns a CSR (the same CSR on every retry). |
| CsrReceived | The CSR's key is recorded. |
| CertIssued | The master signs the certificate (content chosen by the master) and stores its SPKI as the node's **pending pin**, so the master's connections to the node accept either pin. |
| CertInstalled | **cert:** `PUT /agent/v1/pki/rotations/{id}/cert`. The agent checks the certificate (its pending key, its node, a trust anchor), stores it, and from now on presents it to **new** connections; established ones keep the old one. |
| (proof) | The master opens a **fresh** connection pinned to the new SPKI only (no pooled connection, no TLS resume) and reads the rotation. Nothing is committed or deleted before this handshake succeeds. |
| Committing | Persisted on the master; from here on the rotation cannot be aborted. **commit:** `POST /agent/v1/pki/rotations/{id}/commit`, sent over that fresh connection. The agent accepts commit only over a connection that negotiated the pending certificate; it persists "committed" first, then replaces `agent.key`/`agent.crt` (the old key is deleted only now). |
| Committed | The master promotes the pending pin, drops the old one and refreshes the node's client, which replaces both of its handlers (control and data) so pooled connections using the old certificate drain. |

**Abort** (`POST /agent/v1/pki/rotations/{id}/abort`) returns both sides to the old material, but only before
committing; after commit the agent answers 409 `already_committed`. Only one rotation is open per node: a second
start answers 409 `invalid_state` naming `details.activeRotationId`.

**Crash and lost answers.** Every step is a new agent operation and is idempotent per rotation id, so the master may
repeat a step under a new operation id.

- Master crash: the background loop resumes from the stored stage.
- Lost commit answer: the master reads the rotation or re-sends commit (idempotent).
- Agent restart: on start the agent recovers its rotation files before the listener uses them: a committed rotation
  whose promotion was cut short is finished, an installed certificate is presented again, and pending material that no
  longer matches its stage is dropped. Operations the restart left open are closed from what the files show (a step
  that took effect succeeds, one that did not fails and the master repeats it). Nothing is replayed.
- When the agent reports a different stage than the master's record (lost answer, agent restart, an abort the master
  did not record), the record follows the agent while that is safe. A rotation the agent cannot finish before
  committing is aborted on both sides and the next pass starts a new one. An open rotation on the agent that the
  master has no record of is aborted.

**Timing** (`CertificateRotationOptions`):

| Setting | Default |
|---------|---------|
| Renew | 30 days before expiry, i.e. at 60 days of 90 |
| Degraded | From 14 days before expiry while the renewal has not happened (the reason includes the last renewal error) |
| Check interval | Every hour: due renewals, open rotations, master certificate re-issue at half life |

P1 has no "rotate now" button; rotation runs from the background loop. The Nodes page shows the certificate's expiry
and the stage of an open rotation.

---

## 5. Backup v2 and restore

### What a backup holds

The master writes backup schema version 2 (design §5.5, D33, D50); it reads versions 1 and 2.

| Section | Contents |
|---------|----------|
| `models` | Every model definition, including tags, sources and placement |
| `hostServices` | The Services page's container list and restart policies; in P1 under the bootstrap node |
| `master` | The fencing identity at backup time: instance id and epoch |
| `nodes` | The non-secret part of each node record: id, uid, address(es), labels, approved/draining/revoked, memory reserves, default vLLM image, max concurrent starts, current pin (`certSpkiSha256`), certificate expiry, last capabilities. Never join tokens, pending pins or keys |
| `backupDestination` | Where scheduled backups go (no password) |
| `runningModels` | The models meant to be running, per node (`running-models.json`) |
| `agentUpdatesAutomatic` | The fleet's automatic agent updates setting |
| `history` | Benchmark and tuning results (newest first, up to 6 MiB) and start-time measurements |
| `hostUnits` | Each reachable node's managed systemd/Windows services (the Services page's unit whitelist) |
| `secrets` (encrypted, only with a passphrase) | API key records, saved tokens, the backup destination with its SMB password and, with `MirbadLLM:Backup:IncludeCa` (default true), the CA certificate and private key (D9) |

The secrets section is bound to its document version (associated data `mirbadllm-backup/v{schemaVersion}`), so a
version-2 section, which may hold the CA, never decrypts inside a version-1 document (D50). The newer sections are
optional: a file without them reads as before. The master keeps the SMB password (Data Protection) only from the next
time the destination is saved; until then backups carry the destination without it (the Back up tab says so). Each
node's `agent.json` (models and cache roots, approved images, tag pulls, helper image, port range) is not backed up: the
restore preview shows the storage each node last reported and flags differences, and those settings are set on the node.

### Schedule

- **Scheduled:** once a day at `MirbadLLM:Backup:DailyAt` (default `03:00`, the master container's local time) into
  `MirbadLLM:Backup:Directory` (default `/backups`, `MIRBADLLM_BACKUPS` on the host, default
  `/srv/mirbadllm/backups`), pruned to the newest `MirbadLLM:Backup:Keep` (default 14). Scheduled backups carry the
  secrets section only when `MirbadLLM:Backup:Passphrase` (`MIRBADLLM_BACKUP_PASSPHRASE`) is set.
- **On demand:** the Backup page builds a file and hands it to the browser through a one-time link.
- The NAS destination works as before; the master reaches hostd through the agent's backup-mount route.

### Restore

Restore is the Backup page's *Restore* tab (there is no restore API). P1 restores with today's flow, as a
configuration import:

1. The page previews the file (new, changed or identical models, checkpoint folders present, download sizes).
2. The restore writes the selected definitions (a version-2 backup keeps its placements; a version-1 backup's models
   are placed on the bootstrap node), merges API keys by id and restores the Services list of the bootstrap node.
3. It downloads every missing checkpoint folder through the model's node agent, one at a time, and starts each
   auto-start model once its folders are present: sequentially per node, in parallel across nodes, leaving a model
   stopped (reported) when its memory budget does not fit.

The backup's `nodes`, `master` identity and CA are **never imported** by a P1 restore. Restore modes and fleet
disaster recovery onto an empty master are P2 ([P2 guide §10](operations-p2.md#10-backup-and-restore)).

---

## 6. Nodes page and node states

### Node states

The state is computed by the master's node status cache (`NodeState`; design §3.5).

| State | When | Effect in P1 |
|-------|------|--------------|
| **PendingApproval** | The record is not approved | Only `/info` is called; nothing is placed there. The DGX node is pre-approved, so P1 never shows it |
| **Online** | Reachable, protocol negotiated, snapshot applied, no Degraded reason | Normal |
| **Degraded** | Reachable, with at least one reason (below) | Routing works; commands that need the broken subsystem fail with its error |
| **Incompatible** | `/info` answers but no protocol version overlaps, or the agent reports another node id/uid | Blocked: the router answers 503 `node_incompatible`, no auto-start, no commands |
| **Offline** | No contact for 15 s (3 missed heartbeats), or no snapshot yet since the master started | Router answers 503 `node_offline`; starts refused with 503 |
| **PendingTakeover** | The agent accepted a higher epoch, or the same epoch from another master, or a mutation was refused with `stale_master`/`competing_master` | Blocked like Incompatible (section 8) |
| **Revoked** | The record is revoked | Not reachable in P1 (no revoke action) |

**Draining** is a flag on the node record, independent of the state: no new starts or placements, running models
keep serving. P1 has no action that sets it.

### Degraded reasons

Reported by the agent (`HealthReasonCodes`, `/status` and the `health` event):

| Code | Meaning |
|------|---------|
| `docker_unreachable` | Docker does not answer |
| `gpu_probe_failing` | `nvidia-smi` fails |
| `hostd_missing` | hostd's socket is missing on a node that expects it (`HostdExpected`) |
| `model_root_low` | The models root has less than 5 % free space |
| `state_unavailable` | The agent's operation log is not writable |

Computed by the master:

- clock skew between master and agent above 30 s (measured from `/info` when the master (re)connects);
- the agent certificate expires within 14 days and has not been renewed.

`agent_outdated` is not computed in P1 (D53). A connection that does not negotiate HTTP/2 is a connection failure: the
node goes Offline with `http2_not_negotiated: …` as its last error.

### Health in the event stream (MN-047, D68)

The agent checks its health every `Agent:HealthCheckInterval` (default 5 s) and publishes an SSE event `health`
(`HealthEvent`) whenever the set of reason codes changes; a changing error text alone does not produce an event. A
connected master therefore moves a node between Online and Degraded within about one interval, without reconnecting.
An agent that starts with Docker down publishes its first `health` event at the first check.

### The Nodes page (`/nodes`, `/nodes/{id}`)

P1 shows status only; there are no actions (approve, drain, rotate, revoke and takeover are P2:
[P2 guide §4](operations-p2.md#4-nodes-page-and-node-states)).

| Column | Shows |
|--------|-------|
| Node | Display name and id |
| State | State badge, Draining chip, and the reasons |
| Address | The agent address the master dials |
| OS · arch, Memory, GPUs | From the capability report and the last snapshot (free host memory) |
| Models | Ready / placed |
| Agent | Agent version and negotiated protocol |
| Certificate | Expiry and the stage of an open rotation |
| Open operations | Agent operations not yet ended |
| Last seen | Last contact |

---

## 7. Troubleshooting

### Agent start failures

| Message | Likely cause | Fix |
|---------|--------------|-----|
| `The agent is not enrolled: '<dir>' holds no valid agent.crt, agent.key, ca.crt and node-uid.` | Not bootstrap mode and never joined | Set `BootstrapDirectory` (DGX) or run `join` |
| `The agent certificate is for node 'X', but agent.json says 'Y'.` | Node id mismatch | Fix `NodeId`, or re-enroll |
| `Not enrolled yet: waiting for the master's bootstrap bundle in <dir>` | The master has not written the bundle yet | Check the master runs with `BootstrapEnabled` and the shared `mirbadllm-bootstrap` volume |
| `Bootstrap enrollment failed (...): ...; retrying` | The enroll listener is not reachable yet | Check the master's health and that `https://mirbadllm:8443` resolves on the `mirbadllm` network |
| `mirbadllm-agent: configuration file '<path>' not found.` / `<path>: Agent:... ` (exit code 2) | Missing or invalid `agent.json` | Fix the file named in the message |

A Docker daemon that does not answer does not stop the agent: after `DockerStartupTimeout` (2 minutes) it reconciles
anyway, launches fail `docker_unavailable` and the node is Degraded with `docker_unreachable`.

### Node states and fixes

| State | Likely cause | Fix |
|-------|--------------|-----|
| **Offline** | Agent down, Docker network problem, address changed; reasons show the last error | Check `docker ps` and the agent log; the master reconnects with backoff (1 s up to 30 s) |
| **Incompatible** | No common protocol version, or the agent reports another identity | Deploy master and agent from the same commit (they deploy as a pair) |
| **Degraded** | One of the reasons in section 6 | Fix the named subsystem; the node returns to Online at the next health check |
| **PendingTakeover** | Another master, or an older `/data`, drives this node | See section 8 |

### Admin API answers for model commands (D51, D64, MN-047 D67)

`POST /api/models/{id}/start`, `/restart` and `/stop`:

| Status | When |
|--------|------|
| 404 | Unknown model id |
| 409 | Memory admission refused (unless `force`), another model is starting on the node, the model is unplaced, its node is not registered or is revoked, the node is draining (start), or a start ended superseded, cancelled or expired before its container existed |
| 503 | The node is not serving (Offline, Incompatible, PendingTakeover, including a `stale_master`/`competing_master` refusal), the agent is unreachable, or Docker or hostd is down on the node |
| 504 | A node-side timeout or unknown outcome (`timeout`, `outcome_unknown`), or a stop that did not finish in time |

`POST /api/downloads`: 503 when the node is unreachable or Docker/hostd is down on it (was 409 before MN-047); 400
for a bad request, 409 for a conflict (already running, folder exists), 422 for a Hugging Face refusal. The model
folder endpoints answer 503 when the node cannot be read (D62). `GET /api/status` answers 503 while the node has no
fresh memory telemetry (D51).

### Client errors on `/v1` (design §7.4)

The router translates agent outcomes before the response headers are sent, in the OpenAI error shape:

| Status | `code` | When | `Retry-After` |
|--------|--------|------|---------------|
| 404 | `model_not_found` | Unknown served name | – |
| 503 | `model_not_ready` | Model not Ready, or the agent answered `model_not_running` / `not_found` | 30 |
| 503 | `maintenance` | A benchmark or tuning job holds the node | job estimate |
| 503 | `node_offline` | Node not serving (Offline, PendingApproval, PendingTakeover) | 30 |
| 503 | `node_incompatible` | No common protocol version | 300 |
| 503 | `node_unreachable` | The connection to the agent failed now | 10 |
| 503 | `model_unplaced` | The model has no placement | – |
| 503 | `server_busy` | Upload budget or in-flight limit | 5 |
| 413 | `request_too_large` | Body above 64 MiB | – |
| 502 | `upstream_unavailable` | Agent reached, unexpected agent error, or transport failure before the first byte | – |

Since MN-047 (D70) the master accepts the router's full 64 MiB per `/v1` request, with `Content-Length` or chunked,
and answers 413 `request_too_large` above it. Before, Kestrel's default limit (30,000,000 bytes) refused bodies between
the two. Every other master endpoint keeps Kestrel's default.

### Agent error codes

The agent's own errors (`src/MirbadLLM.Agent.Contracts/ErrorCodes.cs`, one HTTP status per code, D44). The most
common:

| Code | HTTP | Meaning |
|------|------|---------|
| `admission_refused` | 409 | The model's memory budget does not fit |
| `start_conflict` | 409 | Another model is starting on the node |
| `stale_master` | 409 | The master's epoch is below the one the agent accepted |
| `competing_master` | 409 | Same epoch, but another master instance or boot |
| `already_committed` | 409 | Abort of a committed rotation |
| `invalid_state` | 409 | The request does not fit the current state (e.g. a second open rotation) |
| `operation_expired` | 410 | The operation envelope expired before it ran |
| `spec_rejected` | 422 | Launch spec violates a guardrail (unapproved digest, writable models mount, ...) |
| `not_supported` | 501 | The agent does not offer this feature |
| `docker_unavailable` | 503 | Docker does not answer |
| `hostd_unavailable` | 503 | The node's service manager is not reachable: hostd on Linux, the Service Control Manager on a Windows node (same code, different wording in the message — MN-239 item 1) |
| `model_not_running` | 503 | The proxied model is not running |
| `server_busy` | 503 | In-flight limit reached |
| `state_unavailable` | 503 | The agent cannot persist the operation |
| `timeout` / `outcome_unknown` | 504 | The node-side action timed out or its outcome is unknown (D42) |
| `insufficient_disk` | 507 | Not enough free space for a download |
| `clock_skew` | 400 | Request time more than 5 minutes off the agent clock |

---

## 8. Master fencing (design §4.2)

### How it works

- **Lock.** The master opens `{DataPath}/master.lock` exclusively for its lifetime and proves the lock is effective; a
  second master on the same volume, or a volume where locking does not work, stops at start.
- **Identity.** `master.json` holds the instance id (created once) and the epoch. The epoch is incremented and written
  to disk on every boot, before any agent call; each boot also gets a random boot id.
- **Agent check.** The agent persists the highest triple it accepted (`fencing.json`). It accepts a higher epoch,
  refuses a lower one with 409 `stale_master`, and at an equal epoch refuses another instance or boot id with 409
  `competing_master`. It re-checks before every destructive stage of an operation.
- A refusal reaches the node state from every caller: the node becomes PendingTakeover (D52), and model commands answer
  503 (section 7).

A normal master restart or redeploy is simply a higher epoch: agents accept it on the next request.

### Takeover

A master whose epoch is below what the agents remember (an older `/data` restored, a copied volume) gets
`stale_master` everywhere. The design's takeover (§4.2 step 3: read the reachable agents' epochs, raise the epoch above
them, write a takeover record, continue for returning agents) is **P2 (MN-115)**. P1 only detects the request: creating
`{DataPath}/takeover`, or starting the master with `MirbadLLM__Fencing__Takeover=true`, logs
`Takeover requested (...). It runs only after the operator confirms the previous master is stopped.` and nothing more.
In P1 the way out is to run the master with its own current `/data`. Remove the option after use: every boot with it
set asks for a takeover again. P2 performs the takeover, with continuation per node and a one-shot
`MirbadLLM:Fencing:TakeoverId` ([P2 guide §9](operations-p2.md#9-takeover-and-continuation)).

---

## 9. Monitoring and diagnostics

### Agent log

Docker captures it (`docker logs mirbadllm-agent`). Useful lines:

- `Agent {NodeId} (uid {NodeUid}) boot {BootId}, version {Version}` – start
- `Enrolled node {NodeId} (uid {NodeUid}) from the bootstrap bundle, key {Spki}` – bootstrap enrollment
- `Reconciled {Operations} open operation(s), rebuilt {Reservations} reservation(s), adopted {Adopted} instance(s); opening the listener` – recovery done
- `Refused a master client certificate: ...` – a TLS client the agent does not trust
- `Refused {Method} {Path}: {Code} ({Message}).` – a refused request (fencing, skew, guardrails)
- `Proxy: {ModelId} sent nothing for {Seconds} s; request ended (the model may be stalled).` – the proxy idle limit
  (`ProxyUpstreamIdleTimeout`) ended a request; see *Stalled and slow models* below

### Master log

`docker logs mirbadllm`. Useful lines:

- `Node {NodeId}: {Previous} -> {Current}. {Reasons}` – node state changes
- `Node {NodeId} is blocked ({Phase}): {Reason}` – incompatible, identity mismatch or pending takeover
- `Master identity {InstanceId}: boot {BootId} at epoch {Epoch} (data path {DataPath}).` – fencing at start
- `Certificate rotation {RotationId} of node {NodeId}: stage {Stage} persisted.` and the `started`, `committed`,
  `aborted`, `stopped at` lines – rotation
- `Certificate renewal of node {NodeId} failed: {Reason}` – a renewal that keeps failing (Degraded from 14 days before expiry)

### Stalled and slow models

A model is Ready when its server answers `GET /health`. That check cannot see an engine that accepts requests and never
finishes them: on 2026-10-17 a vLLM engine froze with 5 requests in flight and answered `/health` for 34 hours while
every request hung. The master therefore also reads each Ready model's last 5 minutes of metrics (the history sampled
every 5 s) and flags two shapes:

| Flag | Rule (every sample in the 5 minutes) | What to do |
|------|--------------------------------------|------------|
| **Stalled** | Requests running, no prompt or generated tokens, KV-cache usage unchanged | Stop and start the model; its log usually shows the last line before it froze |
| **Slow** | Requests running or waiting, under 5 tokens/s generated | Too much load for the model: lower *Max sequences* or the context length, or find the client sending long prompts |

KV-cache growth counts as progress, because vLLM counts prompt tokens only when a prefill completes. One idle sample,
a sample without rates (the first scrape after Ready) or less than 5 minutes of samples clears both flags. The flag
shows on the Overview's *Needs attention* list, the Models list, the model's Overview and the node's model table. It is
a warning only: the model stays Ready and is not restarted automatically.

Requests to a stalled model do not hang forever either: the agent's proxy ends a request when the model sends nothing
for `ProxyUpstreamIdleTimeout` (20 min, agent options above). Before the response headers the client gets 502
`upstream_unavailable` with the message *Model '…' sent no response for … s; it may be stalled or overloaded*. Mid-stream
the stream is aborted. Time spent writing to a slow client does not count.

### Metrics

`/metrics` keeps today's names (memory, GPU, per-model and router metrics); in P1 the host metrics are the DGX node's,
from its telemetry. There are no per-node metrics in P1.

---

## 10. Development and testing

### Two agents on one machine

`tools/dev-multinode` starts a master and two agents with the fake runtime (no Docker models, no GPU, loopback only):

```bash
pwsh tools/dev-multinode/start.ps1 -Reset      # Windows (or PowerShell 7 anywhere)
tools/dev-multinode/start.sh --reset           # Linux / macOS
pwsh tools/dev-multinode/stop.ps1              # or tools/dev-multinode/stop.sh
```

The master runs in Development on `http://127.0.0.1:5080` with its enroll listener on `8443`; agent `dgx` enrolls
from the bootstrap bundle on `9443`, agent `dev-b` joins with a token from the Development-only
`MirbadLLM:DevJoinTokens` helper on `9444` and is approved through `POST /api/nodes/dev-b/approve`. `dgx` is unified
(`unified:64`) and `dev-b` discrete with two fake 16 GiB GPUs (`discrete:2x16`). The scripts write the agent configurations themselves under
`artifacts/dev-multinode`. The [README](../../README.md#multinode-on-one-machine-without-gpus) has the details.

### Tests

- `FakeAgent` (`tests/MirbadLLM.UnitTests/Multinode/Fakes/`) implements the P1 agent surface.
- `AgentContractSuite` (`tests/MirbadLLM.UnitTests/Multinode/Contract/`) runs against `FakeAgent`
  (`FakeAgentContractTests`) and the real agent (`tests/MirbadLLM.UnitTests/Agent/RealAgentContractTests.cs`).
- `Multinode/Exit/` holds the P1 exit tests; `Multinode/V1Compat/` replays recorded `/v1` traffic.
- `Multinode/Chaos/` (category `Chaos`) injects partitions, crashes and lost answers with deterministic seeds. It runs
  in the default test run (about 35 s in parallel).
- `Multinode/Perf/` (category `Perf`): the full harness is opt-in (`MIRBADLLM_PERF=1`, or
  `tools/perf-multinode/run.ps1` / `run.sh`); results are in `docs/multinode/perf/`.
- `DevMultinodeSmokeTests` runs the dev setup's composition in process.

---

## 11. P1 and later phases

### What P1 ships

- One node (`dgx`) on the DGX, master and agent as two containers; clients see no change (same URL, `/v1/models`
  shape, status codes and errors).
- Enrollment: bootstrap bundle only; no UI to add or approve nodes.
- Placement: every model on `dgx`.
- Memory: unified, per-node reserve; no per-GPU budgets.
- Runtimes: vLLM containers only (llama.cpp is P3).
- Two-phase certificate rotation, automatic.
- Fencing: lock, epoch and agent checks; takeover detected but not performed.
- Backup v2 written and read; restore is today's configuration import.
- Nodes page: status only.

### Later (P2 and on)

All of the following is built on `feature/multinode-p2` and described in [operations-p2.md](operations-p2.md):

- Remote Linux nodes with NVIDIA GPUs; join tokens, approval, revocation, rekey (MN-102).
- Placement by node and GPU, per-GPU admission, moves.
- Node-scoped maintenance.
- Takeover with continuation (MN-115), restore modes and fleet recovery (MN-113), audit log and CA rotation (MN-116).
- Nodes page actions (drain, rotate, revoke, takeover banner).

---

## 12. See also

- [operations-p2.md](operations-p2.md) – P2 operator guide and deployment checklist
- [design.md](design.md) – architecture, invariants and the decisions log (Appendix A)
- `deploy/compose.multinode.yaml`, `deploy/ci-deploy-multinode.sh` – the deployment `main` runs

---

*Last updated: 2026-10-07 (MN-131: status, links to the P2 guide)*
