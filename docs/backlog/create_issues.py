"""Create labels, epics and tasks on GitHub from backlog.json using the gh CLI."""
import json, os, subprocess, sys, tempfile

REPO = sys.argv[1] if len(sys.argv) > 1 else "Mohsenbahrzadeh/KSC.MeetingManagement"
HERE = os.path.dirname(os.path.abspath(__file__))
b = json.load(open(os.path.join(HERE, "backlog.json"), encoding="utf-8"))


def gh(*args):
    r = subprocess.run(["gh", *args], capture_output=True, text=True, encoding="utf-8")
    if r.returncode != 0:
        raise SystemExit(f"gh {' '.join(args[:3])} failed: {r.stderr}")
    return r.stdout.strip()


def issue(title, body, labels):
    with tempfile.NamedTemporaryFile("w", suffix=".md", delete=False, encoding="utf-8") as f:
        f.write(body)
    try:
        return gh("issue", "create", "--repo", REPO, "--title", title,
                  "--body-file", f.name, "--label", ",".join(labels))
    finally:
        os.unlink(f.name)


labels = {"epic": "5319e7", "task": "0e8a16", "P0": "b60205", "P1": "d93f0b", "P2": "fbca04"}
labels.update({e["id"]: "1d76db" for e in b["epics"]})
for name, color in labels.items():
    gh("label", "create", name, "--color", color, "--repo", REPO, "--force")

epic_no = {}
for e in b["epics"]:
    tasks = [t for t in b["tasks"] if t["epic"] == e["id"]]
    lst = "\n".join(f"- {t['id']} - {t['title']} ({t['p']})" for t in tasks)
    body = f"**فاز:** {e['phase']}\n\n## تسک‌ها\n{lst}\n"
    url = issue(f"[{e['id']}] {e['title']}", body, ["epic", e["id"]])
    epic_no[e["id"]] = url.rsplit("/", 1)[-1]
    print(e["id"], url, flush=True)

dod = "\n".join(f"- [ ] {d}" for d in b["definitionOfDone"])
for t in b["tasks"]:
    items = "\n".join(f"- {x}" for x in t["body"])
    body = (f"Epic: #{epic_no[t['epic']]} ({t['epic']})\n\n## کارها\n{items}\n\n"
            f"## معیار پذیرش\n{t['accept']}\n\n## تعریف Done\n{dod}\n")
    issue(f"[{t['id']}] {t['title']}", body, ["task", t["p"], t["epic"]])
    print(t["id"], "created", flush=True)
