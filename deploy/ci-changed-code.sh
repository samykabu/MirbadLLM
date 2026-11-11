#!/usr/bin/env bash
# Decides whether a change needs the build, the tests and the deploy (the `changes` job of .github/workflows/ci.yml).
# Reads the changed paths on stdin, one per line, and prints "false" when every path is a Markdown file outside
# deploy/. Prints "true" for anything else, and for an empty list: when nothing is known, CI runs.
# Markdown under deploy/ counts as code: the agent READMEs ship inside the agent bundles and a test reads one.
# `--self-test` checks the rule against known cases (run by the lint job).
set -euo pipefail

needs_ci() {
  local files
  files="$(cat)"
  if [ -z "$files" ] || grep -qvE '\.md$' <<<"$files" || grep -qE '^deploy/' <<<"$files"; then echo true; else echo false; fi
}

if [ "${1:-}" = "--self-test" ]; then
  check() {
    local got
    got="$(printf '%b' "$2" | needs_ci)"
    [ "$got" = "$1" ] || { echo "FAIL: expected $1, got $got for: $2" >&2; exit 1; }
  }
  check false 'README.md\n'
  check false 'README.md\ndocs/setup.md\n.github/PULL_REQUEST_TEMPLATE.md\n'
  check true ''
  check true 'README.md\nsrc/MirbadLLM.Web/Program.cs\n'
  check true 'src/MirbadLLM.Web/Program.cs\nREADME.md\n'
  check true 'deploy/agent/linux/README.md\n'
  check true 'docs/images/screenshots/nodes.png\n'
  check true 'docs/notes.md.txt\n'
  # git quotes a path with non-ASCII characters; an unreadable name runs CI.
  check true '"docs/caf\\303\\251.md"\n'
  echo "self-test passed"
  exit 0
fi

needs_ci
