# Agent updates and release notes — design

Status: design, 2026-10-17. Owner request: the master pushes updates to the Windows, Linux and macOS agents over the
existing mTLS connection. The portal shows which nodes need an update. The admin picks "Update now" (pushed) or the
manual steps per node, or "Update all", or lets the master update agents on its own after each deploy. Every version
carries release notes taken from the merged pull requests (Added, Fixed, Changed). The portal shows them for its own
updates ("What's new") and for each agent update.

Already shipped (#157): the controller and the DGX agent are built with their commit, the sidebar shows MirbadLLM's
version, and the Nodes page shows each agent's version with "Agent from another build".

## 1. Facts this design rests on

| Fact | Where |
|---|---|
| Every `main` commit is a squash-merged PR; a build's identity is `1.0.0+<12-char commit>` | `SourceRevisionId`, `ProductVersion` |
| The master serves every agent bundle anonymously, with SHA-256, size and revision | `AgentBundleEndpoints.cs` (`/agent/bundles`, `X-Content-SHA256`) |
| Each installer has an `upgrade` that keeps identity and config and rolls back if the new agent does not come up | Linux `install-agent.sh` `do_install 1`, Windows `Invoke-Upgrade -Bundle -BundleSha256`, macOS `do_install 1` |
| Agents run unprivileged: Linux `mirbadllm-agent` (`NoNewPrivileges`, empty capability set), Windows `NT SERVICE\mirbadllm-agent` (no write to Program Files, cannot control its own service), macOS `_mirbadllm` | installers, `ScmDenyList.cs` |
| Mutations: `AgentMutation` envelope, fsync'd operation log, restart recovery handlers, master-side `AgentOperationRunner` and `NodeMonitor` closing references | `Envelope.cs`, `IOperationLog`, `AgentReconciler`, `AgentOperationRunner.cs` |
| The DGX agent is a container that CI redeploys with the master | `compose.multinode.yaml`, `ci-deploy-multinode.sh` |
| `/api` accepts only the admin cookie (no API keys), and portal actions use that signed-in session | `Program.cs`, `NodeApiEndpoints.Reauthenticate` |

## 2. Decisions

| # | Decision | Why |
|---|---|---|
| U1 | **Release notes come from the merged PRs**, collected by the deploy job with its own `GITHUB_TOKEN` into `release-notes.json`, baked into the master image. A PR body's `## Release notes` section with `### Added` / `### Fixed` / `### Changed` bullets is used as written; a PR without one is listed under the group its title implies (`Fix…` → Fixed, `Add…`/a new phase → Added, otherwise Changed). | The PRs are where the change is described; no new secret; the notes are part of the build they describe |
| U2 | Notes are **keyed by merge commit**, oldest to newest. "What changed between build A and build B" is every entry after A up to B. | Squash merges make the main history one entry per PR |
| U3 | **The master pushes an update order** to an agent (`POST /agent/update`, mutation `agent-update`). The agent downloads the bundle from the master's own `/agent/bundles/{rid}` and checks its SHA-256 and size against the order. | Reuses the authenticated channel; no new credentials on nodes or in CI |
| U4 | **A small root/SYSTEM helper installs it**, not the agent: Linux a systemd path unit + oneshot service, macOS a LaunchDaemon with `WatchPaths`, Windows a manual-start service the agent may start (its SCM ACL grants only the agent's SID start rights, the same mechanism as `-AllowService`). The helper runs the **new bundle's own** `upgrade` and writes a result file. | The agent stays unprivileged; the installers' upgrade and rollback already exist |
| U5 | **The helper trusts only orders the master signed**: `{nodeUid, rid, version, sha256, size, issuedAt, expiresAt}`, signed with the master's CA key and checked against a root-owned copy of the CA certificate (installed and refreshed by the installer, both roots during a CA rotation). It re-hashes the bundle itself. | The order travels through agent-writable files. Without a signature, a compromised agent account could make the helper run any code as root. With it, it can only install builds the master signed for that node |
| U6 | The agent reports **`agent.update`** only when the helper is installed. The DGX agent (container) never reports it. | The master offers push updates only where they can work |
| U7 | **Outcome**: the order is an operation. Downtime during the upgrade is expected. The new agent's recovery handler closes it as Succeeded when its own version is the target, else Failed with the helper's result (the installer rolled back). The master also closes it when the node reconnects on the target version. | The existing operation lifecycle, nothing new on the wire for status |
| U8 | **Auto-update is a fleet setting** (off by default): when the master starts on a new build, it orders every outdated, reachable (Online or Degraded), `agent.update` node to update, one at a time. A node that is offline is updated when it comes back. | CI needs no admin login; the master already restarts on every deploy |
| U9 | Manual updates stay first-class: each node's dialog shows its OS's exact steps (bundle URL, SHA-256, upgrade command). | Nodes without the helper, and admins who prefer it |

## 3. UI

- **Nodes page**: a callout "N nodes run another build than MirbadLLM 1.0.0 · 826bc75" with **Review updates**. Each row keeps
  its "Agent from another build" alert.
- **Review updates dialog**: one row per outdated node with its version → the target, the notes between the two
  (Added / Fixed / Changed, newest first, each with its PR number), and per node **Update now** (when it reports
  `agent.update`) or **Manual steps**. **Update all** updates every pushable node in turn. The signed-in session authorizes the action.
  Progress per node: Downloading, Installing, Restarting, Updated / Rolled back (with the installer's last lines).
- **Node detail**: the same, for one node, next to the agent version.
- **Settings**: "Update agents automatically when MirbadLLM is updated" uses the current portal session, without asking for the password again. The change remains audited. Manual updates and **Update all** also use that session.
- **Portal "What's new"**: after the master's build changes, the first visit shows a dismissible summary of the PRs
  since the build the viewer last saw (per browser). The sidebar version opens **Releases**, the full history.

## 4. Phases

| Step | Content |
|---|---|
| A | Release notes: deploy-job collector, `release-notes.json` in the image, `/api/releases`, What's new, Releases page, notes in the Review updates dialog with manual steps (no push yet) |
| B | Order signing (master), helper for Linux and macOS (path unit, LaunchDaemon), `POST /agent/update`, `agent.update`, recovery handler, installers install the helper and the root-owned CA copy |
| C | Windows helper service and its ACL in `install-agent.ps1` |
| D | Update now / Update all, progress, auto-update setting |

## 5. As built (step B)

- **Order** (`AgentUpdateOrder`): a header line and `key=value` lines (node-uid, rid, version, sha256, size, issued,
  expires), so the helper reads it with a shell loop. It is signed with the master's issuing CA (`CertificateAuthority.SignData`,
  ECDSA P-256 / SHA-256, DER: what `openssl dgst -verify` checks) and valid for one hour.
- **Agent** (`Agent/Updates`): `POST /agent/update` accepts the order like every mutation (fencing, durable log), checks
  that it is for this node uid and this bundle rid, downloads the bundle from `http://<the master's address as this
  connection saw it>:<BundlePort><BundlePath>`, and checks size and SHA-256. It then writes `bundle.tar.gz`, `operation`,
  `order` and `order.sig` (last) into `{state}/update`. `AgentUpdateWatcher` closes the operation: Succeeded when the
  agent runs the ordered build, Failed with the installer's last lines when the helper reports a non-zero exit, Failed
  after 20 minutes without either.
- **Helper** (`deploy/agent/agent-update-helper.sh`, the same script on Linux and macOS):
  - It runs from a private copy of itself and takes a lock.
  - It copies the spool into a root-only folder, then checks the signature against each certificate in
    `{trust}/ca.pem`, and the order against `{trust}/node-uid`, `{trust}/rid`, the expiry, and the bundle's size and
    hash.
  - It runs the bundle's `install-agent.sh upgrade` with `MIRBADLLM_UPDATE_HELPER=1` and writes `{spool}/result`.
- **Trust** (`/etc/mirbadllm-agent/update` on Linux, `/Library/Application Support/mirbadllm-agent/update` on macOS,
  root-owned):
  - `install`, `upgrade` and `rekey` run by an admin copy the agent's `ca.crt` and node uid there.
  - An upgrade the helper runs keeps them, so the agent can never change what the helper trusts.
  - **After a CA rotation**, each node needs one upgrade or rekey by hand before pushed updates work again; the helper
    refuses orders signed by the new CA until then.
- **Triggers**: Linux `mirbadllm-agent-update.path` (`PathExists=` the signature) starts the oneshot
  `mirbadllm-agent-update.service`; macOS LaunchDaemon `com.mirbadllm.agent-updater` (`WatchPaths`). A pushed
  upgrade never reloads the macOS job, which would kill the helper mid-upgrade.
- **Marker**: `{trust}/helper`. The agent reports `agent.update` only while it exists. A node gets the helper with its
  next upgrade from a bundle that carries it, so updating it by hand once turns pushed updates on.
- **Master**: `POST /api/nodes/{id}/agent-update` (signed-in session; audited as `node.agent-update`) and **Update now**
  on the node's Overview, with progress from the node's open operations and saved operation references. Download,
  Install and Reconnect remain visible across page reloads; Verified requires the agent to report this master's
  build. A failed operation shows its saved error and allows retry; View operations opens the node's Operations
  tab. Reachable Degraded nodes may update, but other health warnings (such as low disk space) remain until resolved.
  Notes and manual commands sit in expandable rows below the action. `BundlePort` is the port of
  `MirbadLLM:NodeApi:AgentBundleUrl`, 8080 by default.

## 6. As built (step C, Windows)

- The helper is the agent's own executable in the `update-helper` mode (`WindowsUpdateHelper`). It is registered by
  `install-agent.ps1` as the manual-start LocalSystem service `mirbadllm-agent-updater`, whose security descriptor
  gives the agent's virtual account only `LC LO RP RC` (query and start). The agent starts it with `sc.exe start` after
  the hand-over.
- `UpdateHelperCore` makes the same checks as the shell helper:
  - the signature, against `C:\Program Files\mirbadllm-agent\update\ca.pem`;
  - the order, against `node-uid`, `rid` and its expiry;
  - the bundle, against its size and hash.
- It then unpacks `bundle.zip` and runs `powershell.exe -File <bundle>\install-agent.ps1 upgrade -Bundle -BundleSha256`
  with `MIRBADLLM_UPDATE_HELPER=1`, writes the result and stops.
- The installer's `install`, `upgrade` and `rekey` (run as administrator) register the service, point it at the
  current agent executable, and save the CA and node uid (`Install-UpdateHelper`); a pushed upgrade keeps the saved
  CA. `uninstall` removes the service.

## 7. Open points

- Downgrades: allowed only as an admin's explicit choice (the order names the version; the helper refuses an order
  whose version is not the bundle's).
- A node whose helper predates a change to the order format is updated manually once.
