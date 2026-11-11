# MirbadLLM multi-node: Operator guide (P2)

What Phase 2 (P2) adds to the [P1 operator guide](operations.md): Linux PC nodes, discrete GPUs, placement by node
and GPU, moves, per-node jobs, takeover, restore modes, the audit log and CA rotation. Read the P1 guide first; this
guide covers only what is new or changed. Every statement here was checked against the code on
`feature/multinode-p2` (Wave 9 review, MN-132; updated by the P2 final review, MN-124). Where the [design](design.md)
says something else, this guide follows the code, and section 16 lists what the design describes but P2 does not ship.

**Status:** P2 is developed but **not deployed**. P1 is live on the DGX (`main` runs `deploy/ci-deploy-multinode.sh`).
P2 reaches the DGX only when the owner merges `feature/multinode-p2` into `main`; section 14 is the runbook for that.

---

## 1. What P2 changes

| Area | P1 | P2 |
|---|---|---|
| Nodes | The DGX only, enrolled from the bootstrap bundle | Plus Linux PCs: join token, approval with a key fingerprint, reject, drain, revoke, rekey |
| Memory | Unified, one start per node | Unified on the DGX; discrete on PCs: per-GPU budgets, one start per GPU |
| Placement | Every model on `dgx` | A node, and GPUs on a discrete node; moves, unplace, force-commit |
| Jobs | One benchmark or tuning run, `/v1` paused | One job per node, at most 2 at once; with two or more nodes only that node pauses |
| Fencing | Takeover detected only | Takeover with Pending takeover and per-node continuation |
| Restore | Configuration import | Configuration import with uid matching, and fleet disaster recovery in recovery mode |
| Audit | None | `{DataPath}/audit/{yyyy-MM}.jsonl`, `/api/audit`, node "Recent commands" |
| CA | Never rotated | Manual CA rotation with dual trust, started by a flag file |

**On the single DGX almost nothing changes.** As long as `dgx` is the only *approved* node, `/v1`, launches, config
hashes, capabilities, admission and benchmark windows behave exactly as in P1. Three visible differences: every browser
logs in once more (section 14), a manual start/stop of a model a job holds is refused with 409 `model_busy`
(section 7), and benchmark runs carry provenance (section 8).

### Fleet members

A node is a **fleet member** when it is approved and not revoked (`NodeFleet`). Every "two or more nodes" rule
counts fleet members only:

- automatic node-scoped maintenance (section 8);
- the stale-instance sweep (section 6);
- the model editor's Placement section and whether a new model needs a placement (`placement_required`).

A node that enrolled and waits for approval serves nothing, takes no placement and changes none of these. With no
approved node at all, the non-revoked nodes count instead.

---

## 2. Before the first PC joins

| Check | Why |
|---|---|
| `MirbadLLM__AdminPassword` is set | Join tokens, rekey tokens, revoke, force-commit and takeover use the signed-in admin session without password re-entry. Without admin login they are refused (403 `reauthentication_failed`, "Admin login is disabled …") |
| The PC can reach the master's enroll listener on TCP 8443 | P2's `deploy/compose.multinode.yaml` publishes it on the DGX (`${MIRBADLLM_ENROLL_PORT:-8443}:8443`, MN-124). It serves only `POST /enroll` and `POST /enroll/rekey` (TLS, a one-time join token, rate-limited per client; everything else answers 404). Allow TCP 8443 from the LAN only, never from the internet (Docker's published ports bypass `ufw`: filter in the router or the `DOCKER-USER` chain). If you change `MIRBADLLM_ENROLL_PORT`, set `MirbadLLM__NodeApi__EnrollUrl` to match |
| The master can reach the PC on TCP 9443 | The master dials every agent. The PC's firewall allows 9443 from the DGX only (section 3) |
| `MirbadLLM__NodeApi__EnrollUrl` | Optional. Without it the join command uses `https://{host of your browser request}:8443`. If you open the UI through a name that does not reach the DGX on 8443 (a reverse proxy), set it, e.g. `https://192.0.2.191:8443` |
| The PC can reach the master's main port (TCP 8080) | The join and rekey commands download the agent from `GET /agent/bundles/{rid}.tar.gz` there (anonymous, read-only, no secrets; the command checks the SHA-256 before it runs anything) |
| `MirbadLLM__NodeApi__AgentBundleUrl` | Optional. Without it the download URL is the address your browser uses for the UI (scheme, host and port). If the PC cannot use that address, set it, e.g. `http://192.0.2.191:8080` |
| An image the PC can pull by digest | A PC refuses tag-only images (section 5.4). The DGX's image is a local build with no registry digest, so a PC needs a vLLM image from a registry, pinned as `repository@sha256:…` |

---

## 3. Adding a Linux PC

Packaging, paths and hardening are in [`deploy/agent/linux/README.md`](../../deploy/agent/linux/README.md). The
short version follows.

**Requirements on the PC:** systemd 247+ (Ubuntu 22.04+, Debian 12+), x86_64 or aarch64, Docker Engine, `curl` and
`sudo`. With NVIDIA GPUs it also needs the NVIDIA Container Toolkit and the NVIDIA driver (`nvidia-smi -L` lists the
GPUs); a PC where `nvidia-smi` lists no GPU joins as a CPU-only node (section 5.6). You need no checkout
and no Docker build: the master image carries ready-built agent bundles for `linux-x64` and `linux-arm64`, and the
join command downloads the right one.

**Agent bundles on the master.** `deploy/Dockerfile` builds both bundles when the image is built (stage
`agent-bundles`, with the publish settings and layout of `install-agent.sh package`, named after the deployed commit)
into `/app/agent-bundles`. The master hashes them once at startup and serves them on the main port:

| Route | Answer |
|---|---|
| `GET /agent/bundles` | `{bundles: [{rid, url, sha256, size, version, built, folder}]}` |
| `GET /agent/bundles/{rid}.tar.gz` | The tarball (`linux-x64` or `linux-arm64`) |

Both routes are anonymous and read-only. They sit outside `/api` (no login, no API key) and outside `/v1`, and the
enroll listener (8443) does not serve them. In recovery mode they answer 503 `recovery_mode`, because no node should
join a master that is restoring. Without bundles (a dev run, not the image) both answer 404 with a message, and the
dialog falls back to the pre-bundle command, which runs in a folder that holds `install-agent.sh` (a checkout's
`deploy/agent/linux/`, or a bundle made with `install-agent.sh package`).

### 3.1 Create the join command (master)

1. **Nodes → Add node.**
2. Fill in:
   - **Node id**: 1–40 lower-case letters, digits and hyphens, starting with a letter or digit. Not `join-tokens`, not
     the id of a revoked node, not an enrolled node.
   - **Display name**: optional, up to 100 characters. It is applied when you approve the node.
   - **Token lifetime**: 10 min, 30 min, 1 h (default), 4 h or 24 h.
   - Your signed-in session authorizes the action.
3. **Create join command.** The dialog shows the Linux command, the enroll URL, the CA hash, the agent bundles
   (version and SHA-256 per architecture) and the expiry.
   - **Paste the token at a prompt** (default): the command has no token; the script asks for it.
   - **Token in the command**: the same command with `sudo MIRBADLLM_JOIN_TOKEN='…'` in place of
     `sudo --preserve-env=MIRBADLLM_JOIN_TOKEN`. The token is briefly visible in `ps` and stays in the shell history.
4. Leave the dialog open. It checks every 2 s and moves on when the agent enrolls. Closing it does not cancel the
   token: the node then shows on the Nodes list for approval. **Withdraw token** expires it.

The token is single-use. Windows and macOS commands are not offered (P4).

API: `POST /api/nodes/join-tokens` `{nodeId, displayName?, ttlMinutes?, rekey: false}` answers
`{nodeId, token, caHash, enrollUrl, expiresAt, purpose, commands: {linux}, agentDownload: {bundles: [{rid, url, sha256,
version}]}}` with `Cache-Control: no-store`. `agentDownload` is null when the master has no bundles.
`DELETE /api/nodes/{id}/join-tokens` expires a node's unused tokens.

### 3.2 Install and join (PC)

Paste the command the dialog shows into a terminal on the PC, in any folder. It is one line (wrapped here):

```bash
(set -e; d="$(mktemp -d)"; trap 'rm -rf "$d"' EXIT; cd "$d"; case "$(uname -m)" in
  x86_64|amd64) u='http://192.0.2.191:8080/agent/bundles/linux-x64.tar.gz' h=<sha256 of linux-x64> ;;
  aarch64|arm64) u='http://192.0.2.191:8080/agent/bundles/linux-arm64.tar.gz' h=<sha256 of linux-arm64> ;;
  *) echo "the master has no mirbadllm agent for $(uname -m)" >&2; exit 1 ;; esac;
  curl -fsSL -o agent.tar.gz "$u"; echo "$h  agent.tar.gz" | sha256sum -c -; tar -xzf agent.tar.gz;
  sudo --preserve-env=MIRBADLLM_JOIN_TOKEN ./mirbadllm-agent-*/install-agent.sh install --node-id pc-1 \
    --enroll-url https://192.0.2.191:8443 --ca-hash sha256:<hash>)
```

The command picks the bundle by `uname -m` and downloads it over plain HTTP into a temporary folder. It checks the
file with `sha256sum -c` against the hash the master put in the command (the command comes from the authenticated
UI), and only then unpacks it and runs its `install-agent.sh install` as root. The folder is removed afterwards. A
wrong hash stops the command with `agent.tar.gz: FAILED` before anything runs. If `/tmp` is mounted `noexec`, set
`TMPDIR` to another folder first.

To add `--address <pc-ip>` and `--firewall-from <dgx-ip>`, append them before the closing `)`:
`… --ca-hash sha256:<hash> --address <pc-ip> --firewall-from 192.0.2.191)`.

When the master has no bundles, the dialog shows the pre-bundle command instead. Run it from a checkout's
`deploy/agent/linux/` or an unpacked bundle:

```bash
sudo ./install-agent.sh --node-id pc-1 --enroll-url https://192.0.2.191:8443 --ca-hash sha256:<hash> \
    --address <pc-ip> --firewall-from 192.0.2.191
```

- `--address` tells the master where to dial the agent. Without it the master records the source address of the
  enroll connection, which behind Docker's port mapping may not be the PC's address. You can fix it later under the
  node's **Settings → Agent address**.
- `--firewall-from` adds a ufw or firewalld rule for TCP 9443 from that source (repeatable; `uninstall` removes it).
- The token is read from `MIRBADLLM_JOIN_TOKEN` or the prompt only; `--token` is refused.

The script creates the user `mirbadllm-agent` (in `docker`, lingering on), installs the binary under
`/usr/local/lib/mirbadllm-agent/`, writes `/etc/mirbadllm-agent/agent.json` from `agent.linux.example.json` (only
if it does not exist), runs `mirbadllm-agent join` as the service user, starts `mirbadllm-agent.service` and waits
until it listens on 9443. The join prints the **key fingerprint**:

```
Key fingerprint (SPKI SHA-256): …
Approve the node on the master's Nodes page; compare the fingerprint above.
```

### 3.3 Approve or reject (master)

The Add node dialog (or the node's row menu, **Approve…**) shows what the agent sent: node, request address, agent
address, OS · arch, GPUs, agent version, enrolled time and the **key fingerprint**.

- **Approve** after the fingerprint matches the one the PC printed. The approval pins exactly that key: if the node
  enrolled again with another key since the dialog opened, the approval is refused ("…enrolled again with another key
  since the dialog was opened; review the new fingerprint."). Approving also expires the node's other unused tokens.
  The master then connects; the node goes Online (or Degraded with its reasons).
- **Reject** deletes the enrollment request and its pending key, and its unused tokens stop working. The agent stays
  installed but cannot connect. A rejected id can be used again (rejecting is not revoking).

API: `POST /api/nodes/{id}/approve` `{expectedSpkiSha256?}` (send the pending pin you checked), `POST
/api/nodes/{id}/reject` (204).

### 3.4 Before the first model on the PC

On the PC, in `/etc/mirbadllm-agent/agent.json`, then `sudo systemctl restart mirbadllm-agent`:

1. `ApprovedImages`: add the vLLM image as `repository@sha256:<64 hex>` (no tag).
2. `DownloadHelperImage`: the same `repository@sha256:…`. The default is the DGX's local tag, which a PC does not
   have, and the helper is created without a pull: the image must already be on the PC (a launch pulls it by digest,
   or pull it by hand first).

On the master, node detail → **Settings**: **Default vLLM image** and **Approved vLLM image digest** (the same
digest). The master sends that digest as `spec.imageDigest` for models without an image of their own. A model with its
own **Image override** needs its own **Image digest** too.

### 3.5 Drain, undrain, revoke

All in the node's row menu (`⋯`) or its detail page.

| Action | Effect | API |
|---|---|---|
| **Drain** | No new starts or placements on the node; running models keep serving | `POST /api/nodes/{id}/drain` |
| **Undrain** | Starts and placements allowed again | `POST /api/nodes/{id}/undrain` |
| **Revoke** | The master stops talking to the node; its pins are deleted and the id can never be used again | `DELETE /api/nodes/{id}` `{confirm, unplaceModels}` |

Revoke in order:

1. Type the node id to confirm; your session authorizes the action.
2. The node is set draining for the duration (lifted again if the revoke is refused).
3. With models placed there and `unplaceModels` false: refused, 409 `models_placed` (the dialog's checkbox "Stop and
   unplace …" sets it).
4. A placed model held by a move, job or restore: refused, 409 `model_busy`; nothing was stopped.
5. Each placed model is stopped (up to `RevokeStopTimeout`, 30 s) and unplaced. Models unplaced before a later failure
   stay unplaced.
6. The node is marked revoked. The agent keeps running; uninstall it on the PC (`install-agent.sh uninstall`).

### 3.6 Rekey (re-pair)

Use it when the agent lost its key or state directory, or its certificate expired (offline for more than 90 days).
The node keeps its uid, approval, models and running containers; only the key and certificate change.

1. Row menu → **Re-pair (rekey)…**, lifetime, **Create rekey command**. Allowed for a node that is
   not revoked and holds a current pin (approved) or a pending pin (enrolled, awaiting approval).
2. On the PC paste the shown command. It downloads and checks the bundle as in 3.2 and ends with

   ```bash
   sudo --preserve-env=MIRBADLLM_JOIN_TOKEN ./mirbadllm-agent-*/install-agent.sh rekey --node-id pc-1 \
       --node-uid <uid> --enroll-url https://<master>:8443 --ca-hash sha256:<hash>)
   ```

   When the master has no bundles, the command is `sudo ./install-agent.sh --rekey --node-id pc-1 --node-uid <uid>
   --enroll-url … --ca-hash …`, run from a checkout or a bundle. The script stops the service, runs `mirbadllm-agent join --rekey` as the service user, and starts the service on
   the new identity. If the rekey fails, the old identity is left as it was and the service starts again on it.
   `--node-uid` is needed only when the state directory is gone.
3. An approved node stays approved. A node that was still pending needs approval as before.

A rekey aborts the node's open certificate rotation and restarts its entry in an open CA rotation. During an open CA
rotation the master reaches the re-paired node with its new client certificate at once (MN-124).

**The DGX's container agent.** The `dgx` node enrolled from the bootstrap bundle, which the master no longer writes
once the node is enrolled; if the agent's state volume (`mirbadllm-agent-state`) is lost, or its certificate
expired, the agent cannot come back by itself. **Re-pair (rekey)…** on `dgx` shows the container procedure instead of
`install-agent.sh` (which would install a second, systemd agent). Run it on the DGX in the deploy checkout:

```bash
export MIRBADLLM_IMAGE=$(docker inspect -f '{{.Config.Image}}' mirbadllm)
export MIRBADLLM_AGENT_IMAGE=$(docker inspect -f '{{.Config.Image}}' mirbadllm-agent)
docker compose -f deploy/compose.multinode.yaml --env-file deploy/.env stop agent
read -rsp 'Rekey token: ' MIRBADLLM_JOIN_TOKEN && export MIRBADLLM_JOIN_TOKEN && echo
docker compose -f deploy/compose.multinode.yaml --env-file deploy/.env run --rm --no-deps -e MIRBADLLM_JOIN_TOKEN agent   join --rekey --enroll-url https://mirbadllm:8443 --ca-hash '<hash>' --node-id 'dgx' --node-uid '<uid>'
unset MIRBADLLM_JOIN_TOKEN
docker compose -f deploy/compose.multinode.yaml --env-file deploy/.env up -d --no-build --no-deps agent
```

The one-off container mounts the same state volume and reaches the enroll listener on the `mirbadllm` Docker
network; model containers keep running and are adopted from their labels. If the agent container was removed, take
the image tags from the last deploy log instead of `docker inspect`. Tested end to end by
`FleetExitTests.TheDgxContainerAgentWithALostStateVolumeIsRePairedByTheDocumentedRekey`.

### 3.7 Certificate rotation of one node

The automatic two-phase rotation of P1 applies to every node (renew at 60 of 90 days). P2 adds **Rotate
certificate…** in the row menu and on the Certificates tab, for an approved node that is serving. There is no HTTP
route for it.

### 3.8 Upgrade and remove a PC agent

To upgrade from a newer bundle, download it from the master (`GET /agent/bundles` lists the URLs and hashes), check
it, unpack it and run its script. A checkout works too.

```bash
curl -fsSL -o agent.tar.gz http://192.0.2.191:8080/agent/bundles/linux-x64.tar.gz
echo "<sha256 from /agent/bundles>  agent.tar.gz" | sha256sum -c - && tar -xzf agent.tar.gz
sudo ./mirbadllm-agent-*/install-agent.sh upgrade    # new binary and unit; agent.json and identity kept
sudo deploy/agent/linux/install-agent.sh upgrade      # the same from a checkout
sudo deploy/agent/linux/install-agent.sh rollback     # previous binary and unit
sudo deploy/agent/linux/install-agent.sh uninstall    # keeps agent.json, identity, model folders; --purge removes them
```

Upgrade the master first. Logs: `journalctl -u mirbadllm-agent`.

---

## 4. Nodes page and node states

### 4.1 States

Same states as P1; P2 makes PendingApproval, PendingTakeover and Revoked reachable.

| State | When | Effect |
|---|---|---|
| PendingApproval | Enrolled, not approved | Only `/info` is called; no placement, no commands |
| Online / Degraded | Reachable, approved | Normal; Degraded lists its reasons |
| Offline | No contact for 15 s | Router 503 `node_offline`; a move from it pauses (section 6) |
| Incompatible | No common protocol, or another node id/uid | Blocked |
| PendingTakeover | Unreachable during a takeover, or it refused this master (`stale_master`/`competing_master`) | Blocked (section 9) |
| Revoked | Revoked | Not listed; `GET /api/nodes?includeRevoked=true` shows it |

**Draining** is a flag: the badge reads "Draining" on an Online node; other states keep their badge plus a "Draining"
chip.

New Degraded reasons computed by the master: `ca_rotation_pending` ("A CA rotation is open ({id}, stage {stage}) and
this node has not committed a certificate under the new CA.") and `placed_gpu_missing` ("placed_gpu_missing: model
'{id}' is placed on GPU(s) {uuid}, which the node no longer reports; …", one per model, MN-124). A Degraded node keeps
serving.

### 4.2 Pages

- **`/nodes`**: Node, State (badge, Draining chip, job chip, reasons), Address, OS · arch, Memory ("Unified · X GiB"
  or "Discrete · X GiB RAM", free), GPUs, Models (Ready / placed), Agent, Certificate, Open operations, Last seen. Row
  menu: Approve…, Reject…, Drain…/Undrain…, Rotate certificate…, Re-pair (rekey)…, Revoke….
- **`/nodes/{id}`** tabs:

| Tab | Shows |
|---|---|
| Overview | State, reasons, fencing state, headroom, capabilities, certificate, open operations, the 5 newest audit lines |
| GPUs | Per GPU: total, used, free, reserve, utilization; "Shares host memory" on the DGX |
| Models | Models placed here, and unplaced models this node runs |
| Operations | The node's benchmark/tuning job and open agent operations |
| Recent commands | Up to 200 audit lines for this node (section 11) |
| Certificates | Expiry, renewal, key fingerprint, pending key, open rotation, CA rotation progress; Rotate and Re-pair buttons |
| Settings | Display name, agent address, other addresses, memory reserve (host RAM reserve on a discrete node), per-GPU reserves, concurrent starts, labels, default vLLM and llama.cpp image and digest |

Settings saves only the changed fields (one `PATCH /api/nodes/{id}`). **Concurrent starts has no effect in P2**: it is
stored on the master but never sent to the agent; the agent uses its own `Agent:MaxConcurrentStarts`.

---

## 5. Discrete GPUs

### 5.1 Memory model per node

The agent decides once at startup whether its node is unified or discrete, and keeps that decision until it restarts.

| `Agent:MemoryModel` | Decision |
|---|---|
| `Unified` | Unified (the DGX: `agent.dgx.json` sets it) |
| `Discrete` | Discrete |
| unset (PC default) | The GPU probe: discrete when a GPU reports its own `memory.total`; no GPU, no `nvidia-smi`, or a failing `nvidia-smi` = unified |

The decision selects admission, the `/status` memory and the P2 capability flags. A unified node reports exactly
P1's features. A discrete node reports `gpus.uuids`, `admission.discrete`, `launch.env`, and `images.digest-pinned`
when `AllowTagPulls` is false.

Agent log lines:

| Id | Text (start) | Meaning |
|---|---|---|
| 2640 | `Memory model {Model} ({why}): admission, /status memory and the P2 capability flags follow it` | The decision; `why` = `forced by the telemetry options`, `no GPU reported` or `N GPU(s) reported` |
| 2641 | `The GPU probe failed while deciding the memory model; the node admits as unified (P1)` | Probe exception |
| 2642 | `nvidia-smi failed while deciding the memory model; …` | Unified until restarted with a working `nvidia-smi` |
| 2643 | `The GPU probe now reports a {Reported} node, but this agent decided {Decided} at startup; …` | Logged once (warning); restart the agent |
| 2644 | `Agent:MemoryModel forces {Decided}; the GPU probe reports a {Reported} node. …` | Logged once (information) on a node forced to a model its probe disagrees with (a PC forced `Unified`); as configured, no restart needed (MN-124) |

### 5.2 Admission on a discrete node

- **One start per GPU.** Starts on different GPUs run side by side; two on the same GPU are serialized. A model
  without GPUs takes the `host` slot. `Agent:MaxConcurrentStarts`, when set, also caps the node.
- **Per GPU:** the model's budget is split evenly over its GPUs; each share must fit that GPU's free memory minus its
  reserve and other starts' shares.
- **Host RAM:** the host part must fit host available memory minus the node's memory reserve (the "host RAM reserve").
  Without `HostMemoryBudgetGiB` the agent estimates `4 × GPUs + 2` GiB; with an explicit `--swap-space S` in the extra
  args the master sends `S × GPUs + 2` GiB, the same estimate the fit preview shows (MN-124).
- **Reserves:** node detail → Settings → `GPU {i} reserve (GiB)` per GPU (keyed by UUID); the host reserve is the
  node's memory reserve.
- A refusal names the part: `… on node pc-1 (GPU 1).` or `(host memory)`. The Dashboard's **Start anyway?** dialog
  shows the refusing GPU and its numbers; force skips memory admission only, not the start rule.

### 5.3 Placing a model on GPUs

The model editor shows a **Placement** section once two or more nodes are fleet members (with one, the node is shown
read-only).

- **New model:** pick the node (draining nodes are disabled; each option shows its state and "fits / does not fit
  now / fit unknown"). The checkpoint folder list and the Hugging Face download act on that node.
- **GPUs** (discrete nodes): tick one or more. Several GPUs mean tensor parallelism (`--tensor-parallel-size N`) and
  must be the same model and memory; GPUs that break that rule are disabled. GPUs are stored by **UUID**, so a
  renumbering after a reboot keeps the card; a GPU that is gone shows "GPU — · no longer on {node}".
- **Fit hint:** free memory after the reserve per GPU and for host RAM. A model that does not fit can still be saved;
  starting asks to confirm.
- **Keep serving during benchmarks and tuning:** the model keeps serving while a job runs on its node (section 8).
- **Image digest:** shown with an image override; `sha256:` + 64 lower-case hex. Needed on PCs.
- **Image:** the empty choice reads "Default image of {node} (…)" when the node has a default image (node settings),
  else "Default image (…)" with the master's `MirbadLLM__VllmImage`; that is what the model launches with.
- **Existing model:** the node is read-only; changing it is a move (section 6). GPU changes on the same node are a
  normal save.

Save refusals:

| Code | Status | When |
|---|---|---|
| `placement_invalid` | 400 | The placement fails validation; `problems` lists each (table below) |
| `placement_changed` | 409 | A move, unplace or another editor changed the placement since you read it; reload |
| `placement_required` | 400 | `POST /api/models` without placement while two or more fleet members exist |
| `use_move` | 400 | `PUT /api/models/{id}` names another node |
| `use_unplace` | 400 | `PUT /api/models/{id}` with `"placement": null` |
| `model_busy` | 409 | A move, job or restore holds the model (section 7) |

Placement problems (`problems[].code`):

| Code | Message (shape) |
|---|---|
| `node_unknown` | `Node 'x' does not exist.` / `Node 'x' is waiting for approval; approve it before placing models there.` |
| `node_revoked` | `Node 'x' is revoked; models cannot be placed there.` |
| `node_draining` | `Node 'x' is draining; it takes no new placements until it is undrained.` |
| `gpus_on_unified_node` | `Node 'x' has unified memory: … clear the GPU selection.` |
| `gpus_required` | `Node 'x' has discrete GPUs; select at least one GPU for the model.` |
| `gpu_unknown` | `GPU {uuid} is not on node 'x' (as of its last report); select one of its GPUs.` |
| `gpu_duplicate` | `GPU {uuid} is listed more than once; pick each GPU once.` |
| `gpu_capacity_mismatch` | `Tensor parallelism needs GPUs of the same model and memory: …` |
| `runtime_unavailable` | The agent does not report `gpus.uuids` (upgrade it), or cannot run vLLM as a container |

At start, a placed GPU the node no longer has answers 422 `gpu_missing` with `code: gpu_missing` in the body ("GPU(s)
… are not on node 'x'; select the model's GPUs again."): edit the model and pick its GPUs again. The node itself reads
**Degraded** with `placed_gpu_missing: model 'x' is placed on GPU(s) …, which the node no longer reports` once its
capability report lacks the card; it keeps serving its other models and clears when the model's GPUs are re-selected
(MN-124).

Fit preview API: `GET /api/nodes/{id}/fit?modelId=…&gpu=<uuid>` or `POST /api/nodes/{id}/fit` with `{modelId?,
definition?, gpuUuids?}`. Numbers come from telemetry no older than 5 s and never exceed the GPU's capacity after its
reserve.

### 5.4 Images on a PC

| Setting | Where | Rule |
|---|---|---|
| `Agent:AllowTagPulls` | agent.json | `false` on a PC (default): tag-only launches and pulls are refused. `true` only on the DGX |
| `Agent:ApprovedImages` | agent.json | Exact `repository@sha256:<64 hex>` entries; launches and pulls must match one |
| Node default vLLM image and digest | Master, node Settings, or `PATCH /api/nodes/{id}` `defaultVllmImage`, `defaultVllmImageDigest` | Sent as `spec.imageDigest` for models without their own image |
| Model `ImageDigest` | Model editor, next to the image override | Pins a model's own image |
| `Agent:DownloadHelperImage` | agent.json | Must be on the PC already |

Refusals are 422 `spec_rejected` with `Launch spec rejected: …`, e.g. `the spec must name an approved image digest`
or `image '…' with digest '…' is not approved on this node`.

`Agent:DownloadHelperUser` (`auto` default, `image`, or `uid[:gid]`): with `auto` the Hugging Face helper runs as the
agent's own uid:gid on Linux when the agent is not root and can write the models root, so the agent can delete what it
downloaded. Otherwise it uses the image's user (root) and logs 2630 `The agent (uid …) cannot write the models root …`.
Folders downloaded as root before keep root ownership (`chown` them once).

### 5.5 Telemetry and pages

- **Dashboard:** grouped by node with two or more nodes; per-GPU headroom on discrete nodes; `?node=` filter.
- **Overview** (which took over Monitoring; `/monitoring` redirects there) and **Services:** a node scope with more than
  one node (`?node=`); on the Overview a scoped discrete node's History has a GPU utilization chart and one "GPU {i}
  memory by model" chart per GPU.
- Per-model GPU memory is measured from hostd (`nvidia-smi --query-compute-apps=gpu_uuid,…`) where hostd runs (the
  DGX); without per-GPU rows it is split evenly over the model's GPUs. PCs run no hostd (`HostdExpected: false`).

---

### 5.6 Nodes without an NVIDIA GPU

`Agent:Accelerator` in `agent.json` says what runs the model weights on a node. The installers write it
(`install-agent.sh --accelerator`, `install-agent.ps1 -Accelerator`), the agent reports it in its capabilities as
`accelerator`, and the master places and labels by it. An NVIDIA or macOS node has no entry and reports none, so
nothing on those nodes changes.

| `Agent:Accelerator` | Node | Memory model | Runtimes | Status |
|---|---|---|---|---|
| unset or `cuda` | NVIDIA GPUs | As section 5.1 | As before | Supported |
| `cpu` | No usable GPU | Unified: the budget counts against host RAM | llama.cpp only | Supported; tested with fakes and the real installers' artefacts, not yet on a dedicated CPU-only machine |
| `vulkan` | AMD or Intel GPUs | Discrete: per-GPU budgets | llama.cpp only | Experimental; not yet verified on AMD or Intel hardware |

**What a `cpu` or `vulkan` node does differently**

- It never runs `nvidia-smi`, also when an unused NVIDIA card is in the machine. A missing `nvidia-smi` does not make
  the node Degraded.
- It lists only llama.cpp among its runtimes. vLLM, SGLang and NIM need CUDA, so placing such a model there is
  refused with `runtime_unavailable` when you save it.
- It downloads from Hugging Face with the agent's own downloader (`downloads.hf-native`), so it needs no vLLM image
  for the download helper.
- A Linux node creates model containers without the NVIDIA device request. A CPU-only container gets no device; a
  Vulkan container gets the DRM render node of its one GPU.

**CPU-only nodes.** The node is unified with no GPU: the Nodes page shows "CPU only" and host RAM, and the GPUs tab
says the node has no supported GPU. A model is placed without GPUs, its whole budget must fit host RAM minus the
node's memory reserve, and one model starts at a time (section 5.2 does not apply). On a Windows node the GGUF
estimate counts every weight in RAM and no CUDA context, whatever **GPU layers** says; the pinned CPU build prints
"no usable GPU found, --gpu-layers option will be ignored". On a Linux node, set the node's default llama.cpp image to `ghcr.io/ggml-org/llama.cpp:server` and
approve its digest (section 5.4).

**Vulkan nodes: where each figure comes from**

| | Windows | Linux |
|---|---|---|
| GPUs listed | Discrete adapters the Vulkan loader reports; integrated GPUs are left out | Cards with `amdgpu` VRAM counters in sysfs; Intel cards are left out |
| Identity | `GPU-` + the Vulkan device UUID | `GPU-pci-` + the PCI address |
| VRAM total | The adapter's device-local Vulkan heaps | `mem_info_vram_total` |
| VRAM in use, all processes | Performance counter `\GPU Adapter Memory(*)\Dedicated Usage`, matched by LUID | `mem_info_vram_used` |
| GPU given to the model | Vulkan device index in `GGML_VK_VISIBLE_DEVICES`, in placement order | The card's `/dev/dri/renderD*` node mapped into the container |
| GPUs per model | Several (llama.cpp's layer split) | One |
| Image or build | The Vulkan build pinned in `llama-cpp.lock.json` | `ghcr.io/ggml-org/llama.cpp:server-vulkan`, approved by digest |

**The admission rule on a Vulkan node.** Free VRAM is VRAM total minus VRAM in use, both read from the machine. The
agent never estimates either. When the figure in use cannot be read for a GPU (the counters are not readable by the
service account, a card has no counter, a sysfs file is missing):

- the GPU's **Used** and **Free** show "—" on the node's GPUs tab;
- the node is Degraded with `gpu_probe_failing` and the reason;
- a start on that GPU is refused (`admission_refused`, "GPU memory unknown") until the figure can be read. **Start
  anyway** skips memory admission, as on any node.

Utilization, power and temperature are not read on a Vulkan node and show "—". On a Windows node the memory a model
takes is still measured at start, from the change in VRAM in use.

**What was checked, and on what.** On a Windows PC with an RTX 5070 and an RTX 5060 Ti (driver 621.01), the Vulkan
source reported the same GPU UUIDs as `nvidia-smi`, VRAM totals of 11 943 and 16 050 MiB against `nvidia-smi`'s 12 227
and 16 311 MiB (the difference is what the driver keeps for itself), and free VRAM 15 and 32 MiB below `nvidia-smi`'s
`memory.free`. On the same PC the pinned Vulkan build listed its devices in the order the agent reads, and
`GGML_VK_VISIBLE_DEVICES` selected and reordered them as the agent expects. No model was loaded through Vulkan, and no
AMD or Intel discrete card, no Linux Vulkan node and no run under the Windows service account has been tested.

## 6. Getting models onto a node, moves and unplace

### 6.1 Ways in

- **Add a new model on the node:** in the editor pick the node first; the folder list and the Hugging Face download
  run on that node's agent.
- **Move an existing model:** the move downloads the checkpoint on the target from the model's Hugging Face sources.
- `/api/downloads*`, `/api/model-folders*` and `/api/catalog` act on the bootstrap node (`dgx`) only in P2. **In P3
  (MN-229) they take an optional `?node=<id>`**: without it the only fleet member acts, exactly as here, and with two
  or more members they answer 400 `node_required`; an unknown or revoked node is 404, one that is not approved yet is
  409 `invalid_state`, and a node that cannot be reached is 503 — except `/api/catalog`, which the master answers from
  the node's last capability report and so stays 200 (operations-p3p4 section 6). The editor (its folder list,
  inspection, catalog and download on the chosen node), a move, or a restore with a target still work as described here.

### 6.2 Move

Editor → Placement → **Move to another node…** (**Move to a node…** for an unplaced model).

1. Pick the target node (draining or non-serving nodes are disabled) and, on a discrete node, its GPUs.
2. **Hugging Face token** (optional): only for a gated or private repo. Kept in the master's memory for this move's
   download, never written to disk, logs or the audit log.
3. Your signed-in session authorizes the move; a running model is stopped during it.
4. **Move to {target}.** The dialog follows the steps; closing it does not stop the move.

| Stage | What happens |
|---|---|
| `downloading` | Every source is downloaded (or completed) on the target; the image is pulled if missing; the folder must be complete |
| `stopping-source` | The model is stopped on the source and the stop is confirmed. If the source is not serving, the move **pauses** here |
| `source-stopped` | Ready to commit |
| `placement-committed` | The registry now places the model on the target (the placement generation goes up) |
| `starting-target` | Only if the model was running: started on the target |
| `done` / `failed` | `failed` = nothing changed; the source is started again if the move had stopped it |

- The move resumes after a master restart from the stored stage (`{DataPath}/ownership.json`). A resumed move has no
  Hugging Face token: a gated download then fails with a hint to start the move again with the token.
- A start on the target refused for good (memory, `gpu_missing`) ends the move `done` with the reason; the placement
  stays committed. Start it from the dialog (**Start {name}**, then **Start anyway?** if memory refuses). Start
  conflicts and transient errors are retried for up to `StartTargetTimeout` (10 min).
- **Cancel move** is allowed at `downloading` and `stopping-source` only.
- A finished move is shown until the master restarts (memory only).
- A model added from a folder (no Hugging Face sources) cannot be moved: 409 `model_not_movable`.
- The commit drops the model's measured start time, so restart and tuning estimates are measured afresh on the target.

API: `POST /api/models/{id}/move` `{targetNodeId, gpuUuids?, gpuIndices?, hfToken?}` → 202 with the record;
`GET /api/models/{id}/move` (record, `canForceCommit`, `forceCommitConsequence`, downloads);
`POST /api/models/{id}/move/cancel`. Refusals: 409 `model_not_movable`, `node_revoked`, `node_draining`, `model_busy`;
400 `placement_invalid`, `invalid_request` (already on that node); 503 `node_offline` (target not serving).

### 6.3 Force-commit

When the source node is not serving (Offline, Incompatible, Pending takeover, …), the move waits at `stopping-source`
and continues by itself when the source comes back. If the source is gone for good:

1. In the move dialog type the **source node id** to confirm.
2. **Force-commit to {target}.**

Consequence (shown in the dialog): *If {source} comes back, the old instance may still be running there until it is
stopped; no traffic is sent to it.* Once the source serves again, the stale-instance sweep stops it.

API: `POST /api/models/{id}/move/force-commit` `{confirm: "<source node id>"}`. Refused with 409
`invalid_state` unless the move is paused at `stopping-source` with a non-serving source; 409
`confirmation_required` for a wrong id (checked before the password).

### 6.4 Unplace

Editor → **Unplace…**: the model is stopped on its node and then placed on no node; it cannot start or serve until it
is moved to a node again. Its folder stays on the node. Password needed only while it runs. API:
`POST /api/models/{id}/unplace` `{}`; 504 when the stop does not finish within `StopTimeout` (2 min).

### 6.5 Stale-instance sweep

Every `SweepInterval` (5 s) the master stops an instance of a known model running on a serving node it is not placed
on (left over after a force-commit), except tuning candidates, unknown containers, models a job or restore holds, and
a move's own instance on its target. It writes a `model.stop` audit line as `system:move`. **It does nothing with
fewer than two fleet members**, so on the single DGX a model you unplace by hand keeps running.

---

## 7. Held models

A move, job, restore or unplace takes an **ownership record** for its model (`{DataPath}/ownership.json`, one record
per model). While it is held:

- a manual **start, stop or restart** (Dashboard, apply-changes dialog, `POST /api/models/{id}/start|stop|restart`) is
  refused with 409 `model_busy`: `Model '{id}' is busy: {holder} holds it.` The UI adds what to do, e.g. "Couldn't
  stop {name}: a benchmark on node dgx is running on it. Wait for it to finish, or stop it from the banner at the top of
  the page.";
- a **placement change or delete** is refused (`model_busy`); other settings and tags can still be saved.

| Holder | Record id | Described as |
|---|---|---|
| move | `move-…` | "a move to node X" |
| job | `job-{jobId}` | "a benchmark on node X" / "a tuning run on node X" |
| restore | `restore-{job}-{model}` | "a restore (configuration import)" |
| unplace | `unplace-…` | "an unplace from node X" |

Boot auto-start and the holders' own work are not checked, so a stale record never blocks auto-start. Stale restore
records are released at boot, stale job records by each node's job recovery.

### Automatic recovery after a node reboot

The master persists the running set in `{DataPath}/running-models.json`, bound to the node uid and agent boot id.
Runtime events and acknowledged starts/stops update it. A new boot queues the previously Starting or Ready models;
instances that the agent adopted are skipped. An ordinary reconnect on the same boot does not launch anything.
Re-enrolling a node with another uid does not inherit the previous machine's running set.

Recovery shares the controller's per-node start lanes with boot auto-start. It waits while any model on that node is
Starting, including after the wait bound, and retries queued work once the slot becomes available. Nodes recover
independently. Launches use normal memory admission (`force: false`); a refused or failed launch is logged and does
not become a crash-restart loop. Offline, draining, blocked and held models wait. Explicit stops cancel pending
recovery, and moved or deleted models are removed from their old node's queue. Temporary benchmark/tuning stops
do not replace the normal running set while their maintenance window is active.

The pending set survives another master restart during recovery. Writes use a flushed temporary file, atomic rename
and directory fsync. If this file cannot be read or persisted, automatic reboot recovery is disabled and logged;
repair the file and restart the controller. The file is local runtime state, outside configuration-import backups.
Initial installation learns the current running set; it does not infer models that stopped before tracking began.
The existing `AutoStart` flag still applies independently at controller boot.

---

## 8. Jobs and maintenance per node

### 8.1 Jobs

- One benchmark or tuning job **per node**, at most `MirbadLLM:Jobs:MaxConcurrent` (default 2) across nodes. A
  second job on a busy node: 409 `job_conflict`; above the limit: 409 `job_limit_reached` naming the running jobs.
- A job runs on its model's node (an unplaced model's job runs on `dgx`). Jobs live in memory; none survives a master
  restart (tuning clean-up runs per node once the node is back).
- Start from the model's Benchmark or Tune page; stop with **Cancel …** there or **Stop …** in the maintenance
  banner (it stops that window's job only).
- API: `GET /api/jobs` (always an array, with `nodeId`), `GET`/`DELETE /api/nodes/{id}/job`. `GET`/`DELETE
  /api/benchmark` and `/api/tuning` keep their P1 shape and act on `dgx` only.

### 8.2 Node-scoped maintenance

`MirbadLLM:Router:Maintenance:NodeScoped`:

| Value | Windows |
|---|---|
| unset (default) | **Automatic:** a job's window is node-scoped when two or more fleet members exist at its start, else global |
| `true` | Every job window is node-scoped |
| `false` | Every window is global (P1) |

Any other value stops the master at startup. The choice is made once per job, before anything is reserved: a node
approved while a job runs leaves that job global until it ends. A global window always gives P1's answers.

| | Global window (P1, single DGX) | Node-scoped window (P2) |
|---|---|---|
| Who is paused | Every model | Models on the job's node, except those with **Keep serving during benchmarks and tuning**; the model under test is always paused |
| `/v1` order | 503 `maintenance` before the API key | 401 → 413 → 503 `server_busy` → 400 → 404 → 503 `maintenance` → node states → `model_not_ready` |
| `/v1/models` | 503 | Ready models minus the paused ones |
| 503 text | `mirbadllm is running a benchmark on {model}; API paused until ~HH:mm UTC` | Same with ` on node {node}` |
| Banner | "API paused for benchmarking — …" | "Node {n} paused for … — …", lists what still serves |

`Retry-After` is the estimate, at least 30 s. The benchmark and tuning start dialogs say which applies (the whole
API, or only the models on node {node}), and check that the GPU of the model's node is idle (MN-124).

### 8.3 Benchmark provenance

Each run records node, node uid, GPU UUIDs, runtime, delivery and image digest, and a noise verdict (`quiet`, `noisy`,
`unknown`: "Noise unknown: …" when telemetry does not cover every run). Each measurement waits up to one 5 s scrape
for the metrics (15 s bound). Comparing runs from different node uid, GPUs, runtime or delivery shows "The runs were
measured on different hardware …; the numbers are not comparable."

---

## 9. Takeover and continuation

A master whose epoch is below what its agents remember (an older `/data` restored, a copied volume) is refused with
`stale_master` (or `competing_master` at the same epoch); those nodes read Pending takeover. A takeover raises the
epoch above every reachable agent's.

**First make sure the previous master is stopped.** A master that still runs takes the nodes back.

### 9.1 Start a takeover (any one of)

| Way | How | Notes |
|---|---|---|
| Banner | **Take over…** in the red banner (shown on every page when a node is Stale or Competing); type `the previous master is stopped` | Needs `MirbadLLM__AdminPassword` |
| API | `POST /api/fencing/takeover` `{confirm: "the previous master is stopped"}` | Works whatever the node states |
| Flag file | Create an empty `{DataPath}/takeover` (on the DGX `/srv/mirbadllm/data/takeover`) | Runs **automatically** within ~5 s, as actor `config`; the file is deleted when it completes |
| Fleet recovery | **Install backup and restart** (section 10.2), after ticking "The previous master is stopped" | Writes the flag file; the takeover runs right after the restart |
| Option | `MirbadLLM__Fencing__Takeover=true` and `MirbadLLM__Fencing__TakeoverId=<unique id>`, restart | Runs automatically at boot, once for that id; remove both keys afterwards |

The flag file, the option and the fleet-recovery install are the confirmation (design D106): nothing else is asked. Without `TakeoverId` the option takes over
again on **every** boot; the UI shows a red warning while the option is set. A failing automatic takeover is retried
with a doubling wait up to 5 min ("The requested takeover failed N time(s) in a row; next attempt in S s.").

The takeover probes every non-revoked node's `/info` (10 s each), moves to `max(epochs) + 1`, writes the record to
`master.json`, and marks the nodes that did not answer **Pending takeover** ("Unreachable during takeover {id} …").

### 9.2 Continue for a returning node

A node that was unreachable and comes back with an epoch below the master's reconnects by itself. One that returns
holding an epoch at or above the master's (another master took it meanwhile) stays blocked: the banner shows
**Continue takeover for {node}…**.

1. Make sure every other master is stopped.
2. Click it, type the **node id** exactly to confirm.

API: `POST /api/fencing/takeover/continue` `{nodeId, takeoverId, agentEpoch, confirm: "<node id>"}` (values
from `GET /api/fencing`). There is no automatic continuation. Right after a takeover or continuation a node may read
Pending takeover or Unknown until its next `/info`; that is not an action.

---

## 10. Backup and restore

Backups are the P1 version 2 format. P2 adds each node's Services list (`hostServices.nodes`) and node default-image
digests. A backup always carries the CA currently issuing (section 12).

### 10.1 Configuration import (default; any master)

Backup → Restore tab → **What to restore: Configuration import**.

- Imports models, tags, sources, API keys (with the passphrase) and Services lists. Never the CA, node trust or
  fencing data.
- A model keeps its node only if a node here has the **same uid**. A node with the same id but another uid ("Name
  only") or no match needs a target: pick a node per model or per backup node, or leave it unplaced. On a discrete
  target the restore chooses the GPUs: as many as the backup's placement had (at least one), the largest equal cards,
  lowest indices first; with no such cards the model is restored unplaced. Either way a job note says what was done;
  change the GPUs in the editor afterwards (MN-124). (`RestoreRequest.targets[].gpuUuids` sets them over the API.)
- Services lists of other nodes are imported only on a uid match.
- Downloads run per node (sequential within a node, parallel across nodes), then auto-start. A model a job, move or
  edit holds is skipped with a note.
- Options (each on by default when the backup has it): **Start the models that were running** (started like auto-start
  models, one at a time per node, left stopped when memory does not fit); **Apply node settings** to the node with the
  same uid (reserves, default images and digests, concurrent starts, labels, name; and the agent-update setting; never
  approval, pins or addresses); **Restore benchmark, tuning and start-time history** (results already here are kept);
  **Connect the backup destination again** (needs the SMB password from the encrypted section, or an NFS export; uses the
  signed-in session like the Back up tab). The Services option also adds back each node's managed systemd/Windows
  services where they are missing, with the same session check as the Services page.

### 10.2 Fleet disaster recovery (empty master only)

Rebuilds the whole fleet's master from a backup: identity, CA, nodes with their pins, models with placements, keys,
Services lists. Use it when the master's `/data` is lost and the nodes still run.

Requirements: a version 2 backup **with the passphrase** and the CA in its secrets (`MirbadLLM:Backup:IncludeCa`,
default true). The master must be **empty**: no node but an unenrolled bootstrap node, no model but the unchanged seed
model, no API key, no used join token. Otherwise the install is refused (`master_not_empty` 409, the page lists what
blocks it; `backup_unsuitable` 400 for an unusable backup).

Runbook (DGX):

1. Keep the old master stopped.
2. Start the master on an empty data path in **recovery mode**: create an empty file `{DataPath}/recovery` (DGX:
   `/srv/mirbadllm/data/recovery`), or set `MirbadLLM__Recovery__Mode=FleetRestore`.
3. In recovery mode the master serves only `/login`, `/backup`, `/healthz`, `/v1` (503 `maintenance` "mirbadllm is
   restoring a backup …") and the UI's assets. `/api` and `/metrics` answer 503 `recovery_mode`; other pages redirect
   to `/backup`. No seed model, no new CA on disk, no bootstrap node, no agent connections, no background services.
   A red banner says "Recovery mode."
4. Log in (needs `MirbadLLM__AdminPassword`), Backup → Restore, upload the backup, enter the passphrase, choose
   **Fleet disaster recovery**, check the preview, **Install backup and restart**, tick **The previous master is
   stopped** (the install requests the takeover), and confirm using the signed-in session.
5. The master writes `master.json`, `pki/`, `nodes.json`, `models.json`, `apikeys.json`, the Services lists and a
   `takeover` flag file, removes the marker and exits. Docker's `restart: unless-stopped` starts it again in normal
   mode. A non-empty first-boot `host-services.json` is kept as `host-services.json.pre-recovery`.
6. On that boot the **takeover runs automatically** from the flag file (section 9); the Restore tab says so.
7. Watch the banner for **Continue takeover for …** and handle each node (section 9.2). Nodes noted as never enrolled
   need a join token; nodes whose certificate expired need a rekey (section 3.6).
8. If you used `MirbadLLM__Recovery__Mode`, remove it (a red banner says "MirbadLLM__Recovery__Mode is still set";
   the option is ignored once the install finished, but remove it).

Also installed when the backup has them: the running intent (each node starts the models it no longer runs once it
reconnects), benchmark/tuning history and start times, the agent-update setting, and the backup destination's settings
and password (for later backups; the share stays mounted by the node). Not recovered: metrics, operation logs, Data
Protection keys (users log in again); hostd/SCM unit whitelists stay on the nodes. A UI restore is audited as `system:restore`.

---

## 11. Audit log

| Item | Value |
|---|---|
| Files | `{DataPath}/audit/{yyyy-MM}.jsonl` (UTC month), one JSON line per action; rolls to `{yyyy-MM}.{n}.jsonl` at 16 MiB |
| Retention | 12 months, and at most 256 MiB in total (oldest first) |
| Fields | `time, actor, action, nodeId, modelId, operationId, target, result, errorCode, detail` |
| Actors | `admin:{12 hex of the session id hash}`, `api:{ip}`, `system:autostart`, `system:job`, `system:restore`, `system:move`, `system:pki`, `config` (flag files and options) |
| Results | `succeeded`, `failed`, `refused`, `accepted`, `unknown` |
| Read | Node detail → Recent commands; `GET /api/audit?nodeId=&modelId=&since=&max=` (newest first, `max` 1–1000, default 50) |

Actions: `model.create|update|delete|start|stop|restart|move|move.force-commit|unplace`,
`node.join-token|enroll|approve|reject|drain|undrain|update|revoke|rotate-cert`, `fencing.takeover`,
`fencing.continue`, `pki.ca-rotation`, `job`, `download`, `folder.delete`, `host-service`, `restore`, `api-key`.

Secrets never reach a line: PEM blocks, API keys (`ssk_…`), Hugging Face tokens (`hf_…`), long base64url runs and
`password=`/`token:`-style values become `***`. Refused password checks are audited too.

---

## 12. CA rotation

For a leaked CA key (for example a leaked backup together with its passphrase). It replaces the master CA while both
sides trust both roots, so no node loses its connection. **Not available with a configured CA** (`MirbadLLM:Pki:*`,
topology b): replace the secret and rekey the nodes instead.

There is no button or API. The master checks two flag files in `{DataPath}/pki/` (DGX:
`/srv/mirbadllm/data/pki/`) at its first pass after start and then every 5 minutes. Each flag is deleted once
handled, also when refused (the log then says `CA rotation flag {flag} refused: {reason}`).

### 12.1 Procedure

1. Make sure the nodes you want to keep are Online. Revoke nodes that are gone for good.
2. `touch /srv/mirbadllm/data/pki/rotate-ca` on the DGX.
3. Within 5 minutes the rotation starts and runs on its own:

| Stage | What happens |
|---|---|
| `Started` | New CA written as `pki/ca-next.key` / `ca-next.crt`; `pki/trust.json` lists both roots |
| `AgentsTrusting` | Each enrolled, non-revoked agent gets both roots (`PUT /pki/trust`) |
| `MasterReissued` | The new CA becomes `pki/ca.crt`/`ca.key`; the master's client and enroll-listener certificates are re-issued under it. New join and rekey commands show the new CA hash |
| `AgentsRotating` | Each node's certificate is rotated under the new CA and checked on a fresh connection |
| `Closing` | Once every node rotated: the master and each agent drop the old root; `ca-next.*` is deleted |
| `Closed` | Done. `ClosedEarly` when it was closed with nodes not rotated |

4. Follow it on node detail → Certificates (CA rotation progress) and in the master log (`CA rotation {id} ended
   {stage}.`). Nodes not yet rotated show Degraded `ca_rotation_pending`.
5. **Delete every backup made before the rotation**: they hold the old CA key. Backups made from `MasterReissued` on
   carry the new CA.

No restart is needed, during or after the rotation: enrollment, rekey, join commands, backups and the enroll listener
follow the current CA at use time. A later restart is safe (the master loads the new CA from `pki/ca.crt`).

### 12.2 A node that stays offline

The rotation waits for it, the master keeps both roots, and it is retried every pass. Either bring it back, or:

1. `touch /srv/mirbadllm/data/pki/close-ca-rotation` to close early (allowed from `MasterReissued` on).
2. Each node that had not rotated reads "Not rotated before the rotation was closed early: re-pair it with join
   --rekey." Rekey it (section 3.6).

A node re-paired or newly enrolled during the rotation is adopted under the new CA.

Refusals (logged, flag deleted): a rotation is already open; the master trusts more than one CA outside a rotation
("repair pki/trust.json first"); close-early with no open rotation or before `MasterReissued`; a configured CA.

---

## 13. Configuration reference (new in P2)

### 13.1 Master (`MirbadLLM__…` environment variables)

| Key | Type | Default | Purpose |
|---|---|---|---|
| `MirbadLLM:Router:Maintenance:NodeScoped` | bool or unset | unset = automatic | Section 8.2 |
| `MirbadLLM:Jobs:MaxConcurrent` | int | 2 | Jobs at once across nodes (values below 1 count as 1) |
| `MirbadLLM:NodeApi:EnrollUrl` | https URL | `https://{request host}:{Enrollment:Port}` | Enroll URL in join and rekey commands |
| `MirbadLLM:NodeApi:DefaultJoinTokenLifetime` | TimeSpan | 60 min | Token lifetime when none is given; 10 min – 24 h |
| `MirbadLLM:NodeApi:JoinTokenRetention` | TimeSpan | 30 days | Used or expired token records older than this are pruned when a token is created; ≤ 0 = never |
| `MirbadLLM:NodeApi:RevokeStopTimeout` | TimeSpan | 30 s | Per-model stop wait of a revoke with `unplaceModels` |
| `MirbadLLM:Moves:PollInterval` | TimeSpan | 1 s | Re-read of a running download, pull or stop |
| `MirbadLLM:Moves:RetryInterval` | TimeSpan | 2 s | Retry while a node is not serving |
| `MirbadLLM:Moves:SweepInterval` | TimeSpan | 5 s | Stale-instance sweep period |
| `MirbadLLM:Moves:StopTimeout` | TimeSpan | 2 min | Unplace and stale-stop wait |
| `MirbadLLM:Moves:StartTargetTimeout` | TimeSpan | 10 min | How long a committed move tries to start on the target |
| `MirbadLLM:Moves:ResumeNodeWait` | TimeSpan | 1 min | How long an unplace resumed after a restart waits for its node |
| `MirbadLLM:Recovery:Mode` | `None` / `FleetRestore` | `None` | Recovery mode (section 10.2); remove after use |
| `MirbadLLM:Fencing:TakeoverId` | string | unset | Makes `MirbadLLM:Fencing:Takeover` one-shot (section 9) |

The `MirbadLLM:Moves:*` values are not validated; keep them positive.

### 13.2 Agent (`agent.json`, `Agent` section)

| Field | Type | Default | Purpose |
|---|---|---|---|
| `MemoryModel` | `Unified` / `Discrete` | unset (probe) | Section 5.1; the DGX's `agent.dgx.json` sets `Unified` |
| `DownloadHelperUser` | `auto` / `image` / `uid[:gid]` | `auto` | Section 5.4 |

P1 fields that matter on a PC: `ApprovedImages`, `AllowTagPulls` (false), `DownloadHelperImage`,
`UseContainerNetwork` (false: models on loopback ports), `HostdExpected`/`ManageSystemdUnits`/`ManageBackupMount`
(false), `MemoryReserveGiB` (the example sets 4), `MaxConcurrentStarts`. The template is
`deploy/agent/agent.linux.example.json`.

### 13.3 Files

Master `{DataPath}`:

| File | Contents |
|---|---|
| `audit/{yyyy-MM}.jsonl` | Audit log (section 11) |
| `ownership.json` | Ownership records: moves, jobs, restores, unplaces (section 7) |
| `pending-node-names.json` | Display names given with a join token, applied at approval (not backed up) |
| `pki/trust.json` | Trust anchors and the CA rotation record; only after a CA rotation started |
| `pki/ca-next.key`, `ca-next.crt` | The new CA during a rotation |
| `pki/master-client.previous.crt/.key` | The previous client certificate during a rotation |
| `pki/rotate-ca`, `pki/close-ca-rotation` | CA rotation flag files (section 12) |
| `recovery` | Recovery-mode marker (section 10.2) |
| `recovery-installed.json`, `recovery-staging/` | Fleet recovery install record and staging |
| `host-services.{node}.json` | Services lists of non-bootstrap nodes |
| `host-services.json.pre-recovery` | A first-boot Services list replaced by a fleet recovery |

PC: `/etc/mirbadllm-agent/agent.json` (root:mirbadllm-agent 0640), `/var/lib/mirbadllm-agent/` (identity,
fencing, operation log; 0700), `/usr/local/lib/mirbadllm-agent/`, `/etc/systemd/system/mirbadllm-agent.service`
and its drop-in `10-install.conf`.

---

## 14. P2 deployment checklist (DGX runbook)

For the owner, when `feature/multinode-p2` reaches `main` (plan §4.7 "P2 readiness (MN-124)", which supersedes the
W9 list in plan §3.6).
`ci-deploy-multinode.sh` redeploys the master and agent images as a pair; model containers keep running and are
adopted.

### 14.1 What changes on the DGX

- **No configuration change.** `deploy/.env` as is. Do not set `MirbadLLM__Router__Maintenance__NodeScoped`,
  `MirbadLLM__Recovery__Mode` or `MirbadLLM__Pki__*`. The agent image carries `agent.dgx.json` with the new
  `"MemoryModel": "Unified"`.
- **New port:** the master publishes the enroll listener on TCP 8443 (`deploy/compose.multinode.yaml`, MN-124), for
  the PCs' join and rekey. It answers only `POST /enroll` and `POST /enroll/rekey`; keep it LAN-only (section 2).
- **New on the data volume:** `audit/{yyyy-MM}.jsonl` and `ownership.json` (job holds). No `pki/trust.json` or
  `ca-next.*` until a CA rotation is started.
- **Visible:** every browser logs in once (cookies from before P2 carry no session id and are signed out); a manual
  start/stop/restart of a model a benchmark, tuning run or restore holds answers 409 `model_busy` naming the job;
  benchmark and tuning runs carry provenance and wait up to ~5 s per measurement; audit lines name `admin:{hash}`,
  `system:job`, `system:autostart`.
- **Unchanged:** `/v1` (codes, bodies, order), launches and config hashes, capabilities (no P2 flag), unified admission
  and `/status`, placement generation 0, no stop by the sweep.

### 14.2 Read-only pre-check (no CI build running)

```bash
nvidia-smi --query-gpu=index,uuid,name,memory.total,compute_cap,driver_version --format=csv,noheader,nounits
nvidia-smi --query-compute-apps=gpu_uuid,pid,process_name,used_memory --format=csv,noheader,nounits
```

1. The first: one GB10 row, `memory.total` `[N/A]`. Record it (with the forced unified model a number would not change
   the DGX).
2. The second: the first column equals the GB10's `GPU-…` from (1) on every vLLM row. If not, deploy anyway, but the
   per-GPU model memory will read 0 after the hostd redeploy (the node total stays right); note it as a P3 follow-up.
3. `sudo ss -ltnp 'sport = :8443'` prints no listener: nothing else on the DGX holds 8443 (else the master cannot
   publish it; set `MIRBADLLM_ENROLL_PORT` in `deploy/.env` and `MirbadLLM__NodeApi__EnrollUrl` to match).

### 14.3 Deploy

Merge to `main` (CI runs `deploy/ci-deploy-multinode.sh` on the self-hosted runner), or by hand with no CI build
running: `DRY_RUN=1 bash deploy/ci-deploy-multinode.sh` to review, then `bash deploy/ci-deploy-multinode.sh`.

### 14.4 hostd redeploy (after master and agent are healthy)

Needed for per-GPU model memory; independent of the rest and safe in either order. From a checkout of the deployed
commit:

```bash
sudo deploy/hostd/install-hostd.sh        # upgrades binary and unit, keeps /etc/mirbadllm/hostd.json and the whitelist
systemctl status mirbadllm-hostd
ls -l /run/mirbadllm-hostd/hostd.sock
```

The Services page lists the units; the Dashboard's measured model memory is unchanged. Rollback: re-run the script
from the previous commit.

### 14.5 Live checks

1. `docker logs mirbadllm-agent`: line 2640 "Memory model Unified (forced by the telemetry options)"; no 2642, 2643
   or 2644. The Nodes page (or `GET /agent/v1/capabilities`) lists P1 features only: no `gpus.uuids`, `launch.env`,
   `admission.discrete`.
2. A benchmark: `/v1` 503 `maintenance` before 401, `/v1/models` 503, no node in the text. A manual stop of the
   benchmarked model on the Dashboard shows the holder message and stops nothing.
3. Log in once per browser. Portal actions use that session without another admin-password prompt; login attempts retain their rate limit.
4. No `model.stop` by `system:move` in the audit log; a model unplaced by hand keeps running.
5. A download as the agent (uid 1654): new folders owned 1654:1654 and deletable from the UI.
6. The DGX agent stays Online; `pki/` unchanged; a backup with a passphrase holds the CA and only `dgx`.
7. From another LAN machine: `curl -sk -o /dev/null -w '%{http_code}
' https://<dgx>:8443/api/status` prints `404`
   and `curl -sk -X POST https://<dgx>:8443/enroll -d '{}' -H 'content-type: application/json'` answers 400/403 (the
   listener is reachable and serves nothing else). From outside the LAN 8443 must not answer.
8. The Nodes page shows `dgx` Online with no reason (no `placed_gpu_missing`, no `ca_rotation_pending`).
9. Later, once a Linux PC is approved: a job on it pauses only its node (P2 order on `/v1`), while a node that is only
   pending changes nothing.

The W7 and W8 live lists in plan §3.6 have the remaining checks.

---

## 15. Troubleshooting

### 15.1 Admin API codes (P2)

ProblemDetails with the code in `code`.

| Code | Status | Meaning | Fix |
|---|---|---|---|
| `reauthentication_failed` | 403 | Missing authenticated session, or admin login is disabled | Sign in; configure admin login if disabled |
| `rate_limited` | 429 | 5 login attempts in a minute (per client) | Wait a minute |
| `confirmation_required` | 409 | The typed node id or phrase does not match | Type it exactly |
| `invalid_state` | 409 | The action does not fit the node's or move's state (already approved, not enrolled, not paused, …) | Read the message; reload |
| `node_revoked` | 409 | The node is revoked | Use a new node id |
| `models_placed` | 409 | Revoke with models placed there | Move them, or tick "Stop and unplace" |
| `model_busy` | 409 | A move, job, restore or unplace holds the model | Wait, or stop that action |
| `placement_invalid` | 400 | Placement fails validation (`problems`) | Section 5.3 |
| `placement_changed` | 409 | The placement changed since you read it | Reload and save again |
| `placement_required` | 400 | New model without placement, two or more fleet members | Pick a node |
| `use_move` / `use_unplace` | 400 | `PUT` changes or clears the node | Use `/move` or `/unplace` |
| `node_draining` | 409 | Target node is draining | Undrain it or pick another |
| `model_not_movable` | 409 | No Hugging Face sources | Add it on the target instead |
| `gpu_missing` | 422 | A placed GPU is not on the node (the node reads Degraded `placed_gpu_missing`) | Pick the model's GPUs again |
| `node_offline` | 503 | The node is not serving | Section 4.1 |
| `job_conflict` | 409 | The node already runs a job | Wait or cancel it |
| `job_limit_reached` | 409 | `MaxConcurrent` jobs run | Wait or cancel one |
| `master_not_empty` | 409 | Fleet recovery on a master with state | Use an empty data path |
| `backup_unsuitable` | 400 | Not v2, no passphrase, no CA, or another configured CA | Use a passphrase backup with `IncludeCa` |
| `recovery_mode` | 503 | The master runs in recovery mode | Install a fleet backup, or remove the marker/option |

Enroll listener (`/enroll`, `/enroll/rekey`): 403 `enrollment_refused` (one generic answer: unknown, used or expired
token, wrong purpose, wrong node or uid, revoked node), 429 `rate_limited` (5 per minute per address).

### 15.2 Symptoms

| Symptom | Likely cause | Fix |
|---|---|---|
| `install-agent.sh` cannot reach the enroll URL | A firewall drops 8443 on the way, or the URL names a host that does not reach the DGX | Section 2; set `MirbadLLM__NodeApi__EnrollUrl` |
| The DGX agent waits for a bootstrap bundle after its volume was lost | The node is enrolled, so the master writes none | Re-pair it with the container procedure (section 3.6) |
| Node Degraded `placed_gpu_missing` | A placed GPU was removed or replaced | Edit the model, pick its GPUs again |
| Node stays Offline after approval | Wrong agent address, or 9443 blocked | Settings → Agent address; firewall from the DGX |
| Approve refused: "enrolled again with another key" | The agent re-enrolled | Compare the new fingerprint, approve again |
| A PC reports no P2 flags, admits as unified | `nvidia-smi` failed at agent start (2642), or `MemoryModel: Unified` | Fix the driver, restart the agent |
| 2643 in the agent log | The GPUs changed since the agent started | Restart the agent |
| 2644 in the agent log | `Agent:MemoryModel` forces a model the probe disagrees with | Nothing, if forced on purpose |
| Launch on a PC: `spec_rejected` … approved image digest | Image not in `ApprovedImages`, or no digest | Section 3.4 |
| PC download: helper image missing | `DownloadHelperImage` is the DGX's tag | Set it to a `repo@sha256` present on the PC |
| 2630, folders not deletable | The agent cannot write the models root | Make the root writable by `mirbadllm-agent` |
| Move paused at "Stop it on {source}" | Source not serving | Bring it back, or force-commit |
| Move `done` with "start was refused" | Memory or GPU refused on the target | Start it (Start anyway?) or change GPUs |
| Manual stop answers 409 `model_busy` | A job holds the model | Stop the job from the banner |
| Node Degraded `ca_rotation_pending` | Open CA rotation | Section 12 |
| Red banner "MirbadLLM__Fencing__Takeover is still set" | The option is left set | Remove it (and `TakeoverId`) |
| Login loop after the P2 deploy | Old cookie without session id | Log in once |

### 15.3 Log lines

Master (`docker logs mirbadllm`):

- `Takeover requested ({source}). …`, `The requested takeover failed N time(s) in a row; next attempt in S s.`,
  `MirbadLLM__Fencing__Takeover is still set, but its takeover {id} already completed; …`
- `CA rotation flag {flag} refused: {reason}`, `CA rotation {id} ended {stage}.`
- `MirbadLLM__Recovery__Mode=FleetRestore is still set, but a fleet recovery already finished in {DataPath}; starting
  normally. Remove the option.`
- `Audit line for {action} dropped…` (the audit directory is not writable)

Agent: 2630, 2640–2644 (section 5).

---

## 16. What is not in P2

### P3 and later

- **P3:** llama.cpp runtime (the editor shows it disabled), native processes (`systemd-run --user`), the native
  downloader, model launch env (`ModelDefinition.Env` with secret names), CPU-only nodes.
- **P4:** the Windows agent (no Windows or macOS join command).
- **P5:** master in Kubernetes, `/readyz`, enroll TLS passthrough, external-volume backups, CA rotation as a
  Nodes-page action.

### Designed but not built in P2

| Design | State in the code |
|---|---|
| Health reason `agent_outdated` | Defined, never computed: no recommended-version policy yet (D106; P4) |
| `/metrics` `node` label, `mirbadllm_node_*` series | Not added; `/metrics` keeps P1's series (the label would change every live DGX series; P3, D106) |
| `?node=` on `/api/downloads*`, `/api/model-folders*`, `/api/catalog`; `node_required` | **Added in P3 (MN-229)**, with `GET /api/model-folders/{folder}/gguf`; `/api/services*` still acts on the node the page selects |
| `GET /api/status` `nodes: [...]` | **Added in P3 (MN-229)**, additively: every P2 field stays and `GET /api/nodes` keeps the full view |
| A GPU picker in the Restore tab | The restore chooses a discrete target's GPUs (section 10.1) |
| `POST /api/nodes/{id}/rotate-cert` | No route; the UI action only |
| Node detail Files and Services tabs | Not built (Services is per node on the Services page) |
| Approved images as a Nodes-page action | `ApprovedImages` is in agent.json only |
| `NodeRecord.MaxConcurrentStarts` | Stored, not sent to the agent; the settings hint says so; the agent uses `Agent:MaxConcurrentStarts` (P3) |

---

## 17. See also

- [operations.md](operations.md) – P1 operator guide (architecture, enrollment, mTLS, backup v2, fencing)
- [`deploy/agent/linux/README.md`](../../deploy/agent/linux/README.md) – Linux PC packaging
- [design.md](design.md) – architecture and decisions (Appendix A, D73–D106 for P2)

---

*Last updated: 2026-10-07 (MN-124, P2 final review; first written by MN-131)*
