"""Reproducible cold-workflow SQL plugin benchmark. Preparation/dry-run never invoke a model."""
import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import sysconfig
from urllib.parse import urlparse

REPO = Path(__file__).resolve().parents[1]
BASELINE = REPO / "docs/evidence/sql-server-samples-evaluation-20261017.json"
SUITE = REPO / "docs/evaluation/sql-server-samples-suite.json"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def save_new(path, value):
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")


def host_runtime():
    host_os = {"Windows": "win", "Darwin": "osx", "Linux": "linux"}.get(platform.system())
    machine = (platform.machine() or os.environ.get("PROCESSOR_ARCHITEW6432") or
               os.environ.get("PROCESSOR_ARCHITECTURE") or sysconfig.get_platform()).lower()
    if "arm64" in machine or "aarch64" in machine:
        arch = "arm64"
    elif "amd64" in machine or "x86_64" in machine:
        arch = "x64"
    else:
        raise ValueError("Cannot establish a supported native architecture for this host.")
    if host_os is None:
        raise ValueError("Unsupported host operating system.")
    return f"{host_os}-{arch}"


def module(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), REPO / "scripts" / (name + ".py"))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


def candidate(plugin):
    plugin = plugin.resolve()
    manifest = read(plugin / ".claude-plugin/plugin.json")
    servers = read(plugin / ".mcp.json")["mcpServers"]
    server = servers["sqlscriptdom-analysis"]
    if server.get("args") != ["--stdio"] or server.get("env"):
        raise ValueError("Frozen offline protocol requires --stdio only and no MCP environment overrides.")
    for lsp in read(plugin / ".lsp.json").values():
        mode = lsp.get("initializationOptions", {}).get("sql", {}).get("mode", "offline")
        if mode != "offline" or lsp.get("args") != ["--stdio"] or lsp.get("env"):
            raise ValueError("Frozen offline protocol excludes LSP source/environment overrides or online mode.")
    build = read(plugin / "build.json")
    if build.get("runtime") != host_runtime() or build.get("version") != manifest["version"]:
        raise ValueError("Candidate runtime/version does not match this host and plugin manifest.")
    executable = Path(server["command"].replace("${CLAUDE_PLUGIN_ROOT}", plugin.as_posix())).resolve()
    if not executable.is_relative_to(plugin) or not executable.is_file():
        raise ValueError("Use an extracted native plugin with its MCP executable inside the plugin root.")
    paths = [plugin / name for name in (".claude-plugin/plugin.json", ".mcp.json", ".lsp.json", "build.json")]
    for folder in ("mcp", "server", "skills"):
        paths += [path for path in (plugin / folder).rglob("*") if path.is_file() or path.is_symlink()]
    files = []
    for path in sorted(set(paths)):
        if not path.is_file() or path.is_symlink() or not path.resolve().is_relative_to(plugin):
            raise ValueError("Candidate artifact has a missing or linked runtime/provenance file.")
        files.append({"path": path.relative_to(plugin).as_posix(), "bytes": path.stat().st_size, "sha256": digest(path)})
    return {"path": str(plugin), "name": manifest["name"], "version": manifest["version"],
            "build": build, "runtime_files": files,
            "fingerprint": hashlib.sha256(json.dumps(files, sort_keys=True).encode()).hexdigest()}


def client(claude):
    executable = shutil.which(claude) if claude else shutil.which("claude")
    if not executable:
        raise ValueError("Claude CLI was not found; supply --claude with its executable path.")
    version = subprocess.check_output([executable, "--version"], text=True, encoding="utf-8").strip()
    help_text = subprocess.check_output([executable, "--help"], text=True, encoding="utf-8")
    required = ("--restricted", "--strict-mcp-config", "--setting-sources", "--max-budget-usd", "--plugin-dir")
    if any(flag not in help_text for flag in required):
        raise ValueError("Claude CLI lacks the isolation/budget flags used by the frozen protocol.")
    return {"executable": str(Path(executable).resolve()), "version": version,
            "provider_host": urlparse(os.environ.get("ANTHROPIC_BASE_URL", "https://api.anthropic.com")).hostname,
            "environment_credentials_present": any(bool(os.environ.get(key)) for key in
                ("ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN"))}


def verify_inputs(study, plan):
    manifest = read(study / "manifest.json")
    for name in ("tasks", "rubric"):
        if digest(study / (name + ".json")) != manifest[name + "_sha256"]:
            raise ValueError("Frozen tasks/rubric changed; prepare a new study instead.")
    snapshot = study / "sql-snapshot"
    actual = {path.relative_to(snapshot).as_posix() for path in snapshot.rglob("*") if path.is_file()}
    if actual != {entry["path"] for entry in manifest["files"]}:
        raise ValueError("Frozen snapshot inventory changed.")
    for entry in manifest["files"]:
        path = (snapshot / entry["path"]).resolve()
        if not path.is_relative_to(snapshot.resolve()) or digest(path) != entry["sha256"]:
            raise ValueError("Frozen SQL content changed.")
    if digest(REPO / "scripts/evaluate-understanding.py") != plan["runner_sha256"]:
        raise ValueError("Measured runner changed since preparation; prepare a new study.")
    if digest(Path(__file__)) != plan["controller_sha256"]:
        raise ValueError("Rerun controller changed since preparation; prepare a new study.")
    if candidate(Path(plan["candidate"]["path"]))["fingerprint"] != plan["candidate"]["fingerprint"]:
        raise ValueError("Candidate bits changed since preparation; prepare a new study.")


def prepare(args):
    source, output, plugin = args.source.resolve(), args.output.resolve(), args.plugin.resolve()
    if plugin.is_relative_to(source) or output.is_relative_to(plugin) or plugin.is_relative_to(output):
        raise ValueError("Keep plugin artifacts, SQL source and study outputs in separate directories.")
    artifact, cli = candidate(plugin), client(args.claude)
    if args.repeats < 1 or args.workers < 1 or args.timeout < 1 or not math.isfinite(args.budget) or args.budget <= 0:
        raise ValueError("Repeats/workers/timeout/budget must be positive.")
    module("prepare-sample-study").prepare(source, SUITE, output)
    count = len(read(output / "tasks.json")) * args.repeats * 2
    plan = {"format": "sqlscriptdom-rerun-plan-v1", "prepared_utc": datetime.now(timezone.utc).isoformat(),
            "candidate": artifact, "client": cli, "model": args.model, "effort": "low", "repeats": args.repeats,
            "workers": args.workers, "timeout": args.timeout, "budget": args.budget, "seed": 42,
            "order": "baseline-first", "mode": "cold-fresh-client; persisted analysis cache disabled",
            "runs": count, "maximum_client_reported_budget_usd": count * args.budget,
            "runner_sha256": digest(REPO / "scripts/evaluate-understanding.py"),
            "controller_sha256": digest(Path(__file__)),
            "historical_baseline": str(BASELINE), "historical_baseline_sha256": digest(BASELINE)}
    save_new(output / "rerun-plan.json", plan)
    print(f"Prepared {count} sessions; per-run reported cap ${args.budget:.2f}; no model calls made.")
    print(f"Candidate {artifact['version']}; fingerprint {artifact['fingerprint']}; provider {cli['provider_host']}.")


def run_command(study, plan):
    return [sys.executable, str(REPO / "scripts/evaluate-understanding.py"),
            "--workspace", str(study / "sql-snapshot"), "--tasks", str(study / "tasks.json"),
            "--plugin", plan["candidate"]["path"], "--model", plan["model"],
            "--source-revision", read(study / "manifest.json")["source_revision"],
            "--claude", plan["client"]["executable"], "--effort", plan["effort"],
            "--repeats", str(plan["repeats"]), "--workers", str(plan["workers"]),
            "--timeout", str(plan["timeout"]), "--budget", str(plan["budget"]),
            "--seed", str(plan["seed"]), "--order", plan["order"], "--restricted",
            "--keep-answers", "--keep-transcripts", "--output", str(study / "runs")]


def run(args):
    study = args.study.resolve()
    plan = read(study / "rerun-plan.json")
    verify_inputs(study, plan)
    command = run_command(study, plan)
    if args.dry_run:
        print(json.dumps({"dry_run": True, "sessions": plan["runs"], "command_arguments": command}, indent=2))
        return
    if (study / "runs").exists():
        raise ValueError("Runs already exist (including partial attempts); preserve them and prepare a new study.")
    current = client(plan["client"]["executable"])
    if current["version"] != plan["client"]["version"] or current["provider_host"] != plan["client"]["provider_host"]:
        raise ValueError("Client/provider changed since preparation; prepare a new study.")
    if not current["environment_credentials_present"]:
        raise ValueError("Isolated clients need existing environment credentials. Do not copy login files/tokens into study outputs.")
    print(f"Running {plan['runs']} sessions, baseline first; failures are retained.", flush=True)
    subprocess.run(command, cwd=REPO, check=True)


def grade_template(args):
    study = args.study.resolve()
    tasks = {task["id"]: task for task in read(study / "rubric.json")["tasks"]}
    schedule = read(study / "runs/experiment.json")["schedule"]
    template = {f"run-{entry['index']:04d}": {"criteria": [None] * len(tasks[entry["task_id"]]["criteria"]),
                "citations": None, "material_unsupported_claim": None, "notes": ""} for entry in schedule}
    save_new(study / "grades.template.json", template)
    print("Saved blank grades.template.json; review actual answers, then save completed grades.json. No automatic correctness scores.")


def compare(baseline, current, manifest, plan):
    dimensions = {"source_revision": manifest["source_revision"] == baseline["source_revision"],
                  "corpus_sha256": manifest["corpus_sha256"] == baseline["corpus_sha256"],
                  "rubric_sha256": manifest["rubric_sha256"] == baseline["rubric_sha256"]}
    for key in ("model", "provider_host", "effort", "repeats", "workers", "order", "restricted", "timeout_seconds", "reported_budget_per_run_usd", "cli_version"):
        dimensions[key] = current["experiment"].get(key) == baseline["experiment"].get(key)
    rows = []
    fields = ("mean_elapsed_ms", "reported_total_input_tokens_mean", "input_tokens_mean", "output_tokens_mean",
              "mean_factual_score", "mean_citation_score", "strict_passes", "tool_failures", "oversized_tool_results")
    for arm in ("grep", "plugin"):
        for key in fields:
            old, new = baseline["summary"][arm][key], current["summary"][arm][key]
            rows.append({"arm": arm, "metric": key, "historical": old, "candidate_study": new,
                         "change_percent": 100 * (new / old - 1) if isinstance(old, (int, float)) and old != 0 and isinstance(new, (int, float)) else None})
    return {"comparison": "descriptive historical comparison; use the fresh matched Grep arm as primary control",
            "matching_dimensions": dimensions, "all_recorded_dimensions_match": all(dimensions.values()),
            "mismatches": [key for key, matched in dimensions.items() if not matched],
            "candidate": {key: plan["candidate"][key] for key in ("name", "version", "fingerprint")}, "metrics": rows,
            "within_candidate_study_plugin_change_percent": current["comparisons"],
            "quality_review": "Explicit source review supplied separately; no inferred grades or general superiority proof."}


def report(args):
    study = args.study.resolve()
    plan = read(study / "rerun-plan.json")
    verify_inputs(study, plan)
    if digest(BASELINE) != plan["historical_baseline_sha256"]:
        raise ValueError("Historical baseline changed since preparation.")
    module("report-sample-study").report(study, args.grades.resolve())
    comparison = compare(read(BASELINE), read(study / "reviewed-results.json"), read(study / "manifest.json"), plan)
    (study / "baseline-comparison.json").write_text(json.dumps(comparison, indent=2), encoding="utf-8")
    lines = ["# Candidate versus retained 0.14.0 study", "", comparison["comparison"], "",
             "Recorded setting/data mismatches: " + (", ".join(comparison["mismatches"]) or "none") + ".", "",
             "| Arm | Metric | Historical | Candidate study |", "|---|---|---:|---:|"]
    lines += [f"| {row['arm']} | {row['metric']} | {row['historical']} | {row['candidate_study']} |" for row in comparison["metrics"]]
    (study / "baseline-comparison.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("Saved report.md, reviewed-results.csv/json and baseline-comparison.md/json.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="action", required=True)
    check = commands.add_parser("check", help="Validate candidate files and CLI flags; no model or SQL execution.")
    check.add_argument("--plugin", type=Path, required=True)
    check.add_argument("--claude")
    prep = commands.add_parser("prepare", help="Freeze corpus, rubric, candidate and run settings; no model calls.")
    prep.add_argument("--source", type=Path, required=True)
    prep.add_argument("--plugin", type=Path, required=True)
    prep.add_argument("--model", required=True)
    prep.add_argument("--output", type=Path, required=True)
    prep.add_argument("--claude")
    prep.add_argument("--repeats", type=int, default=2)
    prep.add_argument("--workers", type=int, default=2)
    prep.add_argument("--timeout", type=int, default=180)
    prep.add_argument("--budget", type=float, default=.50)
    execute = commands.add_parser("run", help="Execute prepared paid client sessions, or validate with --dry-run.")
    execute.add_argument("--study", type=Path, required=True)
    execute.add_argument("--dry-run", action="store_true")
    template = commands.add_parser("grade-template")
    template.add_argument("--study", type=Path, required=True)
    review = commands.add_parser("report")
    review.add_argument("--study", type=Path, required=True)
    review.add_argument("--grades", type=Path, required=True)
    args = parser.parse_args()
    if args.action == "check":
        artifact = candidate(args.plugin)
        print(json.dumps({"candidate": {key: artifact[key] for key in ("name", "version", "fingerprint")},
                          "build": {key: artifact["build"].get(key) for key in ("runtime", "gitCommit", "dirtySource", "selfContained")},
                          "client": client(args.claude), "model_calls": 0}, indent=2))
    else:
        {"prepare": prepare, "run": run, "grade-template": grade_template, "report": report}[args.action](args)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError, subprocess.CalledProcessError) as error:
        print(f"Benchmark stopped: {type(error).__name__}: {error}", file=sys.stderr)
        raise SystemExit(2)
