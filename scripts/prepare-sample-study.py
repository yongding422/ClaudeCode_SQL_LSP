"""Freeze an SQL-only corpus and hidden source-backed rubric before any model runs."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
from datetime import datetime, timezone

spec = importlib.util.spec_from_file_location("evaluation", Path(__file__).with_name("evaluate-understanding.py"))
evaluation = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evaluation)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def prepare(source, suite_path, output):
    source, output = source.resolve(), output.resolve()
    if output.is_relative_to(source) or source.is_relative_to(output):
        raise ValueError("Study and source directories must be separate.")
    suite = json.loads(suite_path.read_text(encoding="utf-8-sig"))
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != suite["source_revision"]:
        raise ValueError("Source revision differs from the reviewed rubric; review it before preparing.")
    for task in suite["tasks"]:
        for relative, first, last in task["evidence"]:
            path = (source / relative).resolve()
            if not path.is_relative_to(source) or not path.is_file() or not 1 <= first <= last <= len(path.read_text(encoding="utf-8-sig").splitlines()):
                raise ValueError(f"Invalid gold evidence: {relative}:{first}-{last}")
    output.mkdir(parents=True, exist_ok=False)
    snapshot = output / "sql-snapshot"
    snapshot.mkdir()
    count = evaluation.copy_sql(source, snapshot)
    files = [{"path": path.relative_to(snapshot).as_posix(), "bytes": path.stat().st_size, "sha256": sha(path)}
             for path in sorted(snapshot.rglob("*.sql"))]
    # copy_sql accepts upper-case .SQL too; include these on case-sensitive hosts.
    if len(files) != count:
        files = [{"path": p.relative_to(snapshot).as_posix(), "bytes": p.stat().st_size, "sha256": sha(p)}
                 for p in sorted(snapshot.rglob("*")) if p.is_file()]
    for task in suite["tasks"]:
        if any(not (snapshot / evidence[0]).is_file() for evidence in task["evidence"]):
            raise ValueError("A rubric evidence file was excluded from the snapshot.")
    rubric = output / "rubric.json"
    rubric.write_text(json.dumps(suite, indent=2), encoding="utf-8")
    tasks = output / "tasks.json"
    tasks.write_text(json.dumps([{key: task[key] for key in ("id", "question")} for task in suite["tasks"]], indent=2), encoding="utf-8")
    manifest = {"prepared_utc": datetime.now(timezone.utc).isoformat(), "source_revision": revision,
                "source_worktree_status": subprocess.check_output(["git", "-C", str(source), "status", "--short"], text=True),
                "snapshot_rules": "SQL only; no symlinks/reparse points; <=2 MiB/file; excludes .git/bin/obj/node_modules/artifacts/.vs/.claude; no source configuration or instructions",
                "rubric_sha256": sha(rubric), "tasks_sha256": sha(tasks), "files": files,
                "corpus_sha256": hashlib.sha256(json.dumps(files, sort_keys=True).encode()).hexdigest(),
                "excluded_oversize_sql": [{"path": p.relative_to(source).as_posix(), "bytes": p.stat().st_size}
                                          for p in source.rglob("*.sql") if p.stat().st_size > 2_097_152]}
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"Frozen {count} SQL files, {len(suite['tasks'])} tasks. Corpus SHA256: {manifest['corpus_sha256']}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--suite", type=Path, default=Path(__file__).resolve().parents[1] / "docs/evaluation/sql-server-samples-suite.json")
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    prepare(arguments.source, arguments.suite, arguments.output)
