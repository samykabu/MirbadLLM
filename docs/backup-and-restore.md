# Backup and restore

**Backup & restore** (`/backup`, admin only) saves the platform's configuration to one JSON file and brings it
back after the master's machine is reinstalled. Every checkpoint is downloaded again from the model's Hugging Face
source, so the file stays small.

| In the backup | Not in the backup |
|---|---|
| Every model definition, including its **tags**, **download sources** and placement | Checkpoint weights (re-downloaded from each model's source) |
| Which models were running, so a restore can start them again | `deploy/.env` (admin password, passphrases, paths): keep your own copy |
| Node settings, managed host services and their restart policies | The host agent's systemd whitelist (`/var/lib/mirbadllm-hostd/whitelist.json`, root-owned on the host) |
| Benchmark, tuning and start-time history, and the agent-update setting | Login cookies' signing keys (everyone signs in again) |
| With a passphrase, encrypted: API key records (SHA-256 hashes and metadata), saved access tokens and the backup share's password | |
| Where scheduled backups go (the share's server, share, user and version) | |

- **Format:** `mirbadllm-backup-<host>-<yyyyMMdd-HHmmss>.json` (UTC), with the creation time, app version and
  host name. The single controller writes `schemaVersion: 1`. The multi-node master writes `schemaVersion: 2`, which
  adds the master's fencing identity, the node records (no secrets) and, in the encrypted section, the CA
  (`MirbadLLM:Backup:IncludeCa`, default true); it reads both versions, and its restore never imports the nodes, the
  master identity or the CA ([operator guide §5](multinode/operations.md#5-backup-v2-and-restore)). The `secrets` section is AES-256-GCM encrypted with a key derived from your
  passphrase by PBKDF2-SHA256 (600,000 iterations, random 16-byte salt, 12-byte nonce; all stored in the file).
  A backup made without a passphrase has no `secrets` section at all. A wrong passphrase, or a changed file, is
  refused before anything is imported.
- **Download now:** the *Back up* tab builds the file and hands it to the browser through a one-time link that
  expires after two minutes (`GET /api/backup/download/{token}`); nothing is written to disk.
- **Scheduled:** once a day at `MirbadLLM:Backup:DailyAt` (default `03:00`, the controller container's local
  time — UTC unless you give the container a `TZ`) a backup is written to `/backups` (`MIRBADLLM_BACKUPS` on
  the host, default `/srv/mirbadllm/backups`, owned by uid 1654) and the directory is pruned to the newest
  `MirbadLLM:Backup:Keep` (default 14) files. Scheduled backups include the API keys only when
  `MIRBADLLM_BACKUP_PASSPHRASE` is set. The *Destination* panel can put that directory on a NAS share
  ([Backup destination](#backup-destination-keep-the-backups-on-a-nas)). The page lists the files with download links
  (`GET /api/backup/files/{name}`), shows the last run's result, and reports a missing or read-only directory
  instead of failing; the directory is never created by MirbadLLM, because a missing directory almost always
  means a missing volume mount. `GET /api/backup/status` returns the same status as JSON.
- **Restore:** the *Restore* tab reads an uploaded file (and its passphrase) and shows a preview first: per model
  *new*, *changed* (which settings) or *identical*, whether its checkpoint folders are already on disk, what will
  be downloaded and how big (when Hugging Face answers), models without a source (*manual*: copy the folder back
  by hand), and how many API keys are new. You pick the models, whether existing ones are replaced or kept, and
  whether to import keys and the Services list; a confirmation states exactly what will happen. The restore then
  runs in the background (it survives a page reload, not a controller restart, and can be cancelled): it writes the
  definitions, merges API keys by id (never duplicating one), downloads every missing checkpoint **one at a time**,
  and starts each auto-start model once all of its folders are present — one model at a time, waiting while
  another model is still starting, and leaving a model stopped (reported on the page) when its memory budget does
  not fit.
- **Model sources:** a model added through the editor's *Download from Hugging Face* records its source
  automatically. For others, fill in the editor's **Source** section (repo id, revision, allow patterns, plus any
  extra checkpoint such as a speculative draft model referenced in Extra args). A model without a source says so
  in the editor: it can't be re-downloaded after a restore.

## Disaster recovery runbook (the master's machine was reinstalled)

1. **Reinstall the OS** (DGX OS / Ubuntu 24.04 with the NVIDIA driver) and confirm `nvidia-smi` works.
2. **Docker + NVIDIA Container Toolkit:** install Docker Engine, install `nvidia-container-toolkit`, run
   `sudo nvidia-ctk runtime configure --runtime=docker && sudo systemctl restart docker`, and check
   `docker run --rm --gpus all ubuntu nvidia-smi`. Add your user to the `docker` group.
3. **Directories:** `sudo install -d -o 1654 -g 1654 /srv/mirbadllm/data /srv/mirbadllm/backups` and
   `sudo install -d /srv/mirbadllm/models /srv/mirbadllm/cache`. The NAS share for the backups is reconnected
   from the page after step 6 (below).
4. **Clone the repo** to `~/mirbadllm`, `cp deploy/.env.example deploy/.env`, and fill it in from your
   saved copy: `DOCKER_GID` (`stat -c %g /var/run/docker.sock`), `MIRBADLLM_ADMIN_PASSWORD`,
   `MIRBADLLM_BACKUP_PASSPHRASE`, paths.
5. **The vLLM image:** pull or build the image `MIRBADLLM_VLLM_IMAGE` names before restoring, under the same name
   as before, so no model sees a configuration change. The Hugging Face download containers run in this image too.
6. **Start MirbadLLM:** `cd deploy && docker compose up -d --build`, or re-register the self-hosted runner
   (`~/actions-runner`, label `dgx-spark`, as a systemd service) and re-run the CI *Deploy* job if you use it.
   Reinstall the host agent with `sudo bash deploy/hostd/install-hostd.sh` if you use the Services page or a
   NAS backup destination; its systemd whitelist is not in the backup, so add those units again there. Then
   reconnect the NAS share on **Backup & restore → Destination** (server, share, user and the NAS password) so
   the backups on it are listed with download links.
7. **Restore:** open `http://<host>:8080/backup`, log in, go to **Restore**, upload the newest backup
   (from the NAS or a downloaded copy), enter its passphrase, check the preview (replace the seeded
   example model if the backup has it), and confirm. Watch the downloads and starts on the same page.
8. **Afterwards:** copy back any checkpoint the preview marked *manual*, re-run tuning or benchmarks if you
   want their history, and check the Dashboard.

## Backup destination: keep the backups on a NAS

Scheduled backups go to `/srv/mirbadllm/backups` on the host (`/backups` in the controller). That directory is
lost with a reinstall, so point it at a NAS share from the page. You don't need ssh for this. It needs the host agent
(see [Host services](host-services.md)). **Reinstall the agent once for this feature**
(`sudo bash deploy/hostd/install-hostd.sh`), because CI does not redeploy it. An older agent makes the panel say so.

1. **Back up → Destination**: pick **Local disk**, **SMB share** or **NFS export**.
2. SMB: server (e.g. `192.0.2.200`), share name, optional subfolder (must already exist), NAS user, password,
   optional domain, SMB version (`3.0` default; `3.1.1`, `2.1`). NFS: server, export path (e.g.
   `/volume1/backups`), optional subfolder, NFS version (`4.1` default, `3`). On a Synology, give MirbadLLM its own
   user with write access to this one share. For NFS, allow the host's address in the share's NFS permissions and make
   the folder writable by uid 1654.
3. **Test connection** (no admin password needed, nothing changes). The agent mounts the share in a private test
   directory inside its own mount namespace, writes, reads back and deletes a file *as uid 1654* (the controller's
   user), and unmounts again. The result appears inline, e.g. `mount error(13): Permission denied — check the
   username and password`.
4. **Save destination** uses the signed-in admin session and states exactly what happens. The agent runs the same
   test again. Only after it passes does it write the credentials file and a systemd unit, and systemd mounts the
   share at `/srv/mirbadllm/backups` (also at every boot, `nofail`, so a missing NAS never blocks the boot).
   Future backups go there. Backups already on the local disk stay underneath, hidden while the share is mounted.
   If the test or the switch fails, the previous destination stays as it was.
5. **Disconnect** (Local disk → *Disconnect the share*, confirmed in the portal) unmounts the share and deletes the
   unit and the credentials. Backups go to the local disk again. If that directory is missing or not owned by
   1654, the page prints the `install -d` command that fixes it.

The panel shows the effective destination: `//192.0.2.200/backups/mirbadllm (mounted, 1.2 TB free of 4 TB)`,
*Not mounted*, *Not visible yet* (mounted on the host but not inside the container), or *This DGX's disk*. The
scheduler re-checks it before every run. When a share is configured but `/backups` inside the controller is not
that share, the run is **skipped and reported** instead of silently writing to the local disk underneath. Each
backup file records the destination; without a passphrase it has no password, so after a reinstall the restore preview says
"Backups were going to //192.0.2.200/…; re-enter the password to reconnect", and *Use these settings* fills the
form.

**What runs as root (mirbadllm-hostd).** The agent accepts only a share description. The mount point is fixed
(`/srv/mirbadllm/backups`) and callers cannot choose it. Every field except the password is checked against a
small character set: no commas, `=`, quotes, newlines, `%` or `..`, so nothing can add a mount option or a unit-file
line. It writes exactly these files:

| File | Content |
|---|---|
| `/etc/systemd/system/srv-mirbadllm-backups.mount` (0644) | the mount unit (name = `systemd-escape -p --suffix=mount /srv/mirbadllm/backups`), first line `# Managed by mirbadllm-hostd` |
| `/etc/mirbadllm-hostd/backup-share.cred` (root 0600, SMB only) | `username=` / `password=` / `domain=` for `mount.cifs` |
| `/var/lib/mirbadllm-hostd/backup-mount.json` (root 0600) | the non-secret settings shown on the page |

It runs only `/usr/bin/mount`, `/usr/bin/umount`, `/usr/bin/systemctl` (`daemon-reload`, `enable`, `restart`,
`disable --now`, `reset-failed`) and `/usr/bin/stat`, always with an argument vector. The password never appears on
a command line or in a unit, log or status; a backup carries it only inside the passphrase-encrypted section. SMB mounts use
`uid=1654,gid=1654,file_mode=0640,dir_mode=0750,vers=<v>,_netdev,nofail`. NFS mounts use
`vers=<v>,soft,timeo=100,retrans=3,_netdev,nofail` (plus `nolock` for v3). They are `soft` so that an unreachable NAS
makes a backup fail instead of hanging the controller. The agent never touches a unit it did not write: a hand-made
`srv-mirbadllm-backups.mount`/`.automount`, or an `/etc/fstab` mount of the same path, is reported and left
alone. To make room for this, the agent's unit (`deploy/hostd/mirbadllm-hostd.service`) now allows writes to
`/etc/systemd/system` and `/etc/mirbadllm-hostd` (`ReadWritePaths`). It also allows `AF_INET`/`AF_INET6` sockets,
which `mount.cifs`/`mount.nfs` need for the test mount; the agent still listens only on its Unix socket.
`install-hostd.sh` creates `/etc/mirbadllm-hostd` (root 0700).

**Container visibility.** A share mounted on the host *after* the controller started is visible inside the
container only through mount propagation. `deploy/compose.yaml` therefore binds the backups directory with
`propagation: rslave`. That needs the host mount holding `/srv/mirbadllm/backups` to be shared or slave, which is
the default on Ubuntu with systemd. Check with `findmnt -o TARGET,PROPAGATION -T /srv/mirbadllm/backups`. If
the page says *Not visible yet*, redeploy the controller with this compose file (or restart it).

**Undo by hand** (on the host):

```bash
sudo systemctl disable --now 'srv-mirbadllm-backups.mount'
sudo rm '/etc/systemd/system/srv-mirbadllm-backups.mount' /etc/mirbadllm-hostd/backup-share.cred /var/lib/mirbadllm-hostd/backup-mount.json
sudo systemctl daemon-reload
sudo install -d -o 1654 -g 1654 -m 0750 /srv/mirbadllm/backups   # if it is missing or root-owned
```

If you set up the earlier hand-made automount from this README, remove it first (same commands, with the
`.automount` unit and `/etc/mirbadllm/nas.cred`). The page refuses to overwrite it.
