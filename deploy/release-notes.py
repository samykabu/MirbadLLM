#!/usr/bin/env python3
"""Collects MirbadLLM's release notes from the merged pull requests (docs/multinode/agent-updates.md, U1/U2).

The deploy job runs it before building the master image:

    GITHUB_TOKEN=... python3 deploy/release-notes.py --repo owner/name --head <sha> --output deploy/release-notes.json

Every commit on main is a squash-merged PR, so the notes are one entry per PR, keyed by its merge commit, oldest first.
A PR body's "## Release notes" section is used as written:

    ## Release notes
    ### Added
    - ...
    ### Fixed
    - ...
    ### Changed
    - ...

A PR without one is listed under the group its title implies (Fix... -> fixed; Add.../Phase... -> added; else changed).
Only the standard library, so it runs on the runner as is. Without a token or network it writes an empty list and
exits 0: the portal then says the notes are unavailable instead of failing the deploy.

The release workflow (.github/workflows/release.yml) also asks for the body of the GitHub Release:

    ... --commits <file of commit ids, one per line> --markdown release-notes.md

--markdown writes the notes as Markdown, newest PR first, grouped as above; --commits keeps only the PRs merged as one
of those commits (the commits since the previous release). --entries reads the entries from a release-notes.json
written earlier instead of asking GitHub.
"""

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone

GROUPS = ("added", "fixed", "changed")
HEADING = {"added": "added", "new": "added", "features": "added", "fixed": "fixed", "fixes": "fixed",
           "changed": "changed", "changes": "changed", "improved": "changed", "security": "fixed"}
MAX_BULLET = 400


def classify_title(title):
    t = title.strip().lower()
    if re.match(r"^(fix|fixes|fixed|hotfix|bug)\b", t) or t.startswith("fix:"):
        return "fixed"
    if re.match(r"^(add|adds|added|new|introduce|feat|feature)\b", t) or re.match(r"^macos phase \d", t):
        return "added"
    return "changed"


def strip_markdown(text):
    text = re.sub(r"!\[[^\]]*\]\([^)]*\)", "", text)          # images
    text = re.sub(r"\[([^\]]+)\]\([^)]*\)", r"\1", text)       # links -> their text
    text = text.replace("**", "").strip()
    return text[:MAX_BULLET].rstrip() + ("…" if len(text) > MAX_BULLET else "")


def parse_body(body):
    """The bullets of a '## Release notes' section by group, or None when the body has none."""
    if not body:
        return None
    lines = body.replace("\r\n", "\n").split("\n")
    start = next((i for i, l in enumerate(lines) if re.match(r"^##\s+release notes\s*$", l.strip(), re.I)), None)
    if start is None:
        return None
    notes = {g: [] for g in GROUPS}
    group = None
    for line in lines[start + 1:]:
        s = line.strip()
        if re.match(r"^##\s", s) and not s.startswith("###"):
            break
        heading = re.match(r"^###\s+(.+?)\s*$", s)
        if heading:
            group = HEADING.get(heading.group(1).strip().lower().rstrip(":"))
            continue
        bullet = re.match(r"^[-*]\s+(.+)$", s)
        if bullet and group:
            notes[group].append(strip_markdown(bullet.group(1)))
        elif s and group and notes[group] and line.startswith(("  ", "\t")):
            notes[group][-1] = strip_markdown(notes[group][-1] + " " + s)  # a wrapped bullet
    return notes if any(notes.values()) else None


def fetch_merged(repo, token, limit):
    prs, page = [], 1
    while len(prs) < limit and page <= 10:
        url = f"https://api.github.com/repos/{repo}/pulls?state=closed&base=main&sort=updated&direction=desc&per_page=100&page={page}"
        request = urllib.request.Request(url, headers={
            "Accept": "application/vnd.github+json", "Authorization": f"Bearer {token}",
            "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "mirbadllm-release-notes"})
        with urllib.request.urlopen(request, timeout=30) as response:
            batch = json.load(response)
        if not batch:
            break
        prs.extend(p for p in batch if p.get("merged_at") and p.get("merge_commit_sha"))
        page += 1
    return prs[:limit]


def entry(pr):
    notes = parse_body(pr.get("body"))
    title = pr["title"].strip()
    if notes is None:
        notes = {g: [] for g in GROUPS}
        notes[classify_title(title)].append(title)
    return {"number": pr["number"], "title": title, "mergedAt": pr["merged_at"], "commit": pr["merge_commit_sha"],
            "url": pr.get("html_url"), **notes}


def render_markdown(entries):
    """The body of a GitHub Release: one list per group, newest PR first, each bullet with its PR number."""
    lines = []
    for group in GROUPS:
        bullets = [f"- {bullet} (#{e['number']})" for e in reversed(entries) for bullet in e[group]]
        if bullets:
            lines += [f"### {group.capitalize()}", "", *bullets, ""]
    return "\n".join(lines) if lines else "No release notes were collected for this release.\n"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", required=True)
    parser.add_argument("--head", default="")
    parser.add_argument("--output", required=True)
    parser.add_argument("--limit", type=int, default=300)
    parser.add_argument("--entries", help="read the entries from this release-notes.json instead of GitHub")
    parser.add_argument("--commits", help="for --markdown: a file of commit ids; only PRs merged as one of them are listed")
    parser.add_argument("--markdown", help="also write the notes as Markdown to this file")
    args = parser.parse_args()

    entries, error = [], None
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
    if args.entries:
        with open(args.entries, encoding="utf-8") as f:
            entries = json.load(f)["entries"]
    elif not token:
        error = "no GITHUB_TOKEN"
    else:
        try:
            entries = sorted((entry(p) for p in fetch_merged(args.repo, token, args.limit)), key=lambda e: e["mergedAt"])
        except (urllib.error.URLError, TimeoutError, ValueError, KeyError) as ex:
            error = str(ex)

    document = {"generatedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"), "head": args.head,
                "error": error, "entries": entries}
    with open(args.output, "w", encoding="utf-8", newline="\n") as f:
        json.dump(document, f, ensure_ascii=False, indent=1)
        f.write("\n")
    if args.markdown:
        released = entries
        if args.commits:
            with open(args.commits, encoding="utf-8") as f:
                commits = set(f.read().split())
            released = [e for e in entries if e["commit"] in commits]
        with open(args.markdown, "w", encoding="utf-8", newline="\n") as f:
            f.write(render_markdown(released))
    if error:
        print(f"::warning::release notes unavailable ({error}); the portal shows none for this build", file=sys.stderr)
    else:
        print(f"release notes: {len(entries)} merged PRs written to {args.output}")


if __name__ == "__main__":
    main()
