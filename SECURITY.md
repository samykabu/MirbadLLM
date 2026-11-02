# Security policy

## Reporting a vulnerability

Please do not open a public issue, discussion or pull request for a security problem.

Report it privately through GitHub's private vulnerability reporting:

1. Open the repository's **Security** tab.
2. Choose **Report a vulnerability**.
3. Describe the problem, the affected version or commit, and the steps to reproduce it. Include the configuration
   that matters (for example whether an admin password or API keys are set) and the impact you expect.

A maintainer acknowledges the report, works on a fix in a private advisory, and credits you in the advisory unless
you ask not to be named. Please give us a reasonable time to release a fix before you disclose the problem.

## Supported versions

MirbadLLM has no long-term support branches. Fixes land on `main`, and the latest commit on `main` is the supported
version.

## Deployment notes

MirbadLLM is built for a trusted local network. Some defaults favour a first run over a hardened one, so check these
before you expose anything:

- Set `MIRBADLLM_ADMIN_PASSWORD`. Without it the UI and the admin API are open, and every page says so.
- Create an API key, or set `MIRBADLLM_REQUIRE_API_KEY=true`. Until the first key exists, `/v1` accepts requests
  without one.
- Port 8080 serves plain HTTP. Put a TLS reverse proxy in front of it for anything beyond the LAN.
- Keep the enroll listener (8443) and the agents' port (9443) on the LAN. Allow 9443 from the master's address only.
- The host agent runs as root. It acts only on whitelisted units and refuses a compiled-in deny list, but install it
  only where you need the Services tab or a NAS backup destination.
- Backups made with a passphrase carry secrets in an encrypted section. Keep the passphrase somewhere other than the
  machine you back up.
