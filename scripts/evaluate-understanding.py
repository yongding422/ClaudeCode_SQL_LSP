"""Paired Claude task runner. Aggregate metrics omit SQL, paths, prompts and answers.

Does not enable persistent server metrics or hooks. Accuracy scores require independent review; they are
never inferred from tool counts or a model's assertion that its answer is correct.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import csv
import json
import math
import os
from pathlib import Path
import random
import signal
import shutil
import subprocess
import tempfile
import time
from urllib.parse import urlparse


def summarize(events):
    calls, results = {}, {}
    malformed = 0
    initial, final = {}, {}
    for event in events:
        if not isinstance(event, dict):
            malformed += 1
            continue
        if event.get("type") == "system" and event.get("subtype") == "init":
            initial = event
        if event.get("type") == "result":
            final = event
        message = event.get("message", {})
        for block in message.get("content", []) if isinstance(message, dict) else []:
            if not isinstance(block, dict):
                continue
            if block.get("type") == "tool_use":
                calls[block["id"]] = block.get("name", "unknown")
            if block.get("type") == "tool_result":
                results[block["tool_use_id"]] = block

    def category(name):
        if name in ("LSP", "Grep", "Read", "Glob", "Skill"):
            return name.lower()
        if name.startswith("mcp__"):
            return "mcp"
        return "other"

    counts = dict.fromkeys(("lsp", "grep", "read", "glob", "skill", "mcp", "other"), 0)
    failures, missing, oversized = 0, 0, 0
    server_timings, report_bytes, analysis_caches = [], [], []
    for call_id, name in calls.items():
        counts[category(name)] += 1
        result = results.get(call_id)
        if result is None:
            missing += 1
        elif "exceeds maximum allowed tokens" in json.dumps(result.get("content", "")):
            failures += 1
            oversized += 1
        elif result.get("is_error") or "Error performing " in json.dumps(result.get("content", "")):
            failures += 1
        elif name.startswith("mcp__") and name.endswith(("__find_procedures", "__analyze_procedure")):
            content = result.get("content", "")
            texts = [content] if isinstance(content, str) else [b.get("text", "") for b in content
                    if isinstance(b, dict) and b.get("type") == "text"] if isinstance(content, list) else []
            for text in texts:
                try:
                    payload = json.loads(text)
                except (ValueError, TypeError):
                    continue
                if not isinstance(payload, dict):
                    continue
                timings = payload.get("timings")
                if isinstance(timings, dict):
                    server_timings.append(timings)
                if name.endswith("__analyze_procedure") and isinstance(payload.get("report"), dict):
                    # Canonical report bytes, not wire frame size or model token usage. No contents retained.
                    report_bytes.append(len(json.dumps(payload["report"], ensure_ascii=False, separators=(",", ":")).encode("utf-8")))
                    cache = payload.get("cache")
                    if isinstance(cache, dict) and isinstance(cache.get("enabled"), bool):
                        analysis_caches.append((cache["enabled"], cache.get("status")))
                break

    def timing_sum(field):
        values = [t[field] for t in server_timings if isinstance(t.get(field), (int, float)) and
                  not isinstance(t[field], bool) and math.isfinite(t[field]) and t[field] >= 0]
        return round(sum(values), 3) if values else None

    cold = [t["coldStart"] for t in server_timings if isinstance(t.get("coldStart"), bool)]
    usage = final.get("usage", {}) or {}
    return {**{key + "_calls": value for key, value in counts.items()}, "tool_failures": failures,
            "missing_tool_results": missing, "oversized_tool_results": oversized, "malformed_events": malformed,
            "outcome": final.get("subtype", "missing-result"), "model_observed": initial.get("model"),
            "input_tokens": usage.get("input_tokens"), "output_tokens": usage.get("output_tokens"),
            "cache_read_tokens": usage.get("cache_read_input_tokens"),
            "cache_write_tokens": usage.get("cache_creation_input_tokens"),
            "reported_cost_usd": final.get("total_cost_usd"), "server_timed_calls": len(server_timings),
            "server_cold_start_calls": sum(cold) if cold else None,
            **{"server_" + label + "_ms": timing_sum(field) for label, field in
               (("queue", "queueMs"), ("initialization", "initializationMs"), ("navigation", "navigationMs"),
                ("analysis", "analysisMs"), ("presentation", "presentationMs"),
                ("cache_validation", "cacheValidationMs"), ("cache_read", "cacheReadMs"), ("cache_write", "cacheWriteMs"))},
            "analysis_cache_enabled_calls": sum(enabled for enabled, _ in analysis_caches) if analysis_caches else None,
            "analysis_cache_hits": sum(status == "Hit" for _, status in analysis_caches) if analysis_caches else None,
            "analysis_reports": len(report_bytes), "analysis_report_utf8_bytes": sum(report_bytes) if report_bytes else None}


def copy_sql(source, destination):
    excluded = {".git", "bin", "obj", "node_modules", "artifacts", ".vs", ".claude"}
    count = 0
    for root, dirs, files in os.walk(source, followlinks=False):
        dirs[:] = sorted(d for d in dirs if d not in excluded and not linked(Path(root, d)))
        for name in sorted(files):
            path = Path(root, name)
            if path.suffix.lower() != ".sql" or linked(path) or path.stat().st_size > 2_097_152:
                continue
            target = destination / path.relative_to(source)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(path, target)
            count += 1
    return count


def linked(path):
    return path.is_symlink() or bool(getattr(path.lstat(), "st_file_attributes", 0) & 0x400)


def run_one(args, task, arm, repeat, index):
    destination = args.output / f"run-{index:04d}"
    destination.mkdir(parents=True, exist_ok=False)
    # Fresh SQL-only workspaces hide gold answers, project instructions and task files.
    with tempfile.TemporaryDirectory(prefix="sql-understanding-") as temporary:
        workspace = Path(temporary).resolve()
        files = copy_sql(args.workspace, workspace)
        tools = "Read,Grep,Glob" if arm == "grep" else "Read,Grep,Glob,LSP,Skill,ToolSearch"
        allowed = tools
        if arm == "plugin":
            manifest = json.loads((args.plugin / ".claude-plugin/plugin.json").read_text(encoding="utf-8-sig"))
            prefix = f"mcp__plugin_{manifest['name']}_sqlscriptdom-analysis__"
            allowed += "," + prefix + "find_procedures," + prefix + "analyze_procedure"
        command = [args.claude, "--print", "--verbose", "--output-format", "stream-json", "--model", args.model,
                   "--effort", args.effort, "--setting-sources", "", "--settings", '{"disableAllHooks":true}',
                   "--strict-mcp-config", "--mcp-config", '{"mcpServers":{}}', "--no-session-persistence",
                   "--permission-mode", "dontAsk", "--tools", tools, "--allowedTools", allowed,
                   "--max-budget-usd", str(args.budget)]
        if getattr(args, "restricted", False):
            command += ["--restricted"]
        if arm == "plugin":
            command += ["--plugin-dir", str(args.plugin)]
            manifest = json.loads((args.plugin / ".claude-plugin/plugin.json").read_text(encoding="utf-8-sig"))
            config = json.loads((args.plugin / ".mcp.json").read_text(encoding="utf-8-sig"))
            server = config["mcpServers"]["sqlscriptdom-analysis"]
            server["command"] = server["command"].replace("${CLAUDE_PLUGIN_ROOT}", args.plugin.as_posix())
            server["args"] = [value.replace("${CLAUDE_PLUGIN_ROOT}", args.plugin.as_posix()) for value in server.get("args", [])]
            # Strict isolation also excludes plugin-autoloaded MCP servers. Enable only the bundled analysis entry.
            command[command.index("--mcp-config") + 1] = json.dumps({"mcpServers": {f"plugin:{manifest['name']}:sqlscriptdom-analysis": server}})
        else:
            command += ["--disable-slash-commands"]
        workflow = ("Use the SQL plugin's find_procedures and analyze_procedure tools for relevant modules first; "
                    "use Read/Grep/Glob to verify or fill gaps. " if arm == "plugin" else
                    "Use Read/Grep/Glob to investigate the SQL source. ")
        command += [task["question"] + "\n" + workflow +
                    "Answer in at most 450 words with file:line citations, direct versus transitive effects where relevant, "
                    "and explicit unknowns. Do not execute SQL or run shell commands. Report tool failures honestly."]
        env = os.environ.copy()
        env["CLAUDE_CONFIG_DIR"] = str(destination / "isolated-config")
        env.pop("SQLLSP_MCP_CONFIG", None)  # Frozen offline experiment; no accidental online profile.
        env.pop("SQLLSP_ANALYSIS_CACHE_DIR", None)  # A normal cold experiment must not inherit a local report cache.
        env.pop("SQLLSP_METRICS_DIR", None)  # Keep unsolicited local metric writes out of the frozen experiment.
        env["ENABLE_LSP_TOOL"] = "1"
        start = time.perf_counter()
        raw, stderr, timed_out = "", "", False
        # Use pipes without shell interpolation. communicate drains both streams.
        try:
            with subprocess.Popen(command, cwd=workspace, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                  text=True, encoding="utf-8", errors="replace", start_new_session=os.name != "nt",
                                  creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0) as process:
                try:
                    raw, stderr = process.communicate(timeout=args.timeout)
                except subprocess.TimeoutExpired:
                    timed_out = True
                    if os.name == "nt":
                        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True, check=False)
                    else:
                        os.killpg(process.pid, signal.SIGKILL)
                    raw, stderr = process.communicate()
                exit_code = process.returncode
        except OSError as error:
            exit_code = -1
            stderr = str(error)
        events, malformed = [], 0
        for line in raw.splitlines():
            try:
                events.append(json.loads(line))
            except ValueError:
                malformed += 1
        metrics = summarize(events)
        metrics["malformed_events"] += malformed
        if timed_out:
            metrics["outcome"] = "timeout"
        if exit_code != 0 and metrics["outcome"] == "success":
            metrics["outcome"] = "process-error"
        violation = arm == "grep" and any(metrics[name + "_calls"] for name in ("lsp", "mcp", "skill", "other"))
        if violation:
            metrics["outcome"] = "baseline-tool-violation"
        if args.keep_transcripts:
            (destination / "session.jsonl").write_text(raw, encoding="utf-8")
            (destination / "stderr.log").write_text(stderr, encoding="utf-8")
        if args.keep_answers:
            final = next((e for e in reversed(events) if isinstance(e, dict) and e.get("type") == "result"), {})
            (destination / "answer.txt").write_text(str(final.get("result", "")), encoding="utf-8")
        row = {"task_id": task["id"], "arm": arm, "repeat": repeat, "order": index, "model": args.model,
               "effort": args.effort, "repo_revision": args.revision, "cli_version": args.cli_version,
               "plugin_version": args.plugin_version if arm == "plugin" else None,
               "start_condition": "fresh-client-process; OS/model caches uncontrolled", "sql_files": files,
               "elapsed_ms": round((time.perf_counter() - start) * 1000), "exit_code": exit_code,
               **metrics, "dependency_precision": None, "dependency_recall": None, "behavior_score": None,
               "citation_score": None, "unsupported_handling_score": None, "reviewer_corrections": None}
        (destination / "metrics.json").write_text(json.dumps(row, indent=2), encoding="utf-8")
        return row


def make_schedule(tasks, repeats, seed, order="random", arms=("grep", "plugin")):
    pairs = [(task, repeat) for task in tasks for repeat in range(1, repeats + 1)]
    rng = random.Random(seed)
    rng.shuffle(pairs)
    if order == "baseline-first":
        return [(task, arm, repeat) for arm in arms for task, repeat in pairs]
    schedule = []
    for task, repeat in pairs:
        shuffled = list(arms)
        rng.shuffle(shuffled)
        schedule.extend((task, arm, repeat) for arm in shuffled)
    return schedule


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workspace", required=True, type=Path)
    parser.add_argument("--tasks", required=True, type=Path, help="JSON array of {id, question}; never include hidden gold.")
    parser.add_argument("--plugin", required=True, type=Path)
    parser.add_argument("--model", required=True, help="Exact model/version shared by both arms.")
    parser.add_argument("--source-revision", help="Reviewed original source revision when using an SQL-only snapshot.")
    parser.add_argument("--claude", default=shutil.which("claude"))
    parser.add_argument("--effort", choices=("low", "medium", "high"), default="low")
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--order", choices=("random", "baseline-first"), default="random")
    parser.add_argument("--arms", nargs="+", choices=("grep", "plugin"), default=["grep", "plugin"])
    parser.add_argument("--workers", type=int, default=1, help="Bounded concurrent clients; phases never overlap in baseline-first mode.")
    parser.add_argument("--restricted", action="store_true", help="Require a Claude CLI supporting workspace-confined file tools.")
    parser.add_argument("--budget", type=float, default=0.50, help="Reported USD limit per run, not guaranteed subscription billing.")
    parser.add_argument("--timeout", type=int, default=120)
    parser.add_argument("--output", type=Path, default=Path("artifacts/understanding-evaluation") / str(int(time.time())))
    parser.add_argument("--keep-answers", action="store_true", help="Opt in to local answers that can contain private SQL details.")
    parser.add_argument("--keep-transcripts", action="store_true", help="Opt in to local raw sessions that can contain private SQL details.")
    args = parser.parse_args()
    args.workspace, args.plugin, args.output = args.workspace.resolve(), args.plugin.resolve(), args.output.resolve()
    if not args.claude or args.repeats < 1 or args.budget <= 0 or args.timeout < 1 or args.workers < 1 or len(set(args.arms)) != len(args.arms):
        parser.error("Claude executable, positive repeats/budget/timeout are required.")
    if not args.workspace.is_dir() or not (args.plugin / ".claude-plugin/plugin.json").is_file():
        parser.error("Workspace and built plugin must exist.")
    if args.output.is_relative_to(args.workspace):
        parser.error("Output must be outside the SQL workspace to keep evidence out of model input.")
    tasks = json.loads(args.tasks.read_text(encoding="utf-8-sig"))
    if not isinstance(tasks, list) or not tasks or any(set(t) != {"id", "question"} for t in tasks):
        parser.error("Tasks must contain only id and question; gold belongs outside the runner.")
    if len({t["id"] for t in tasks}) != len(tasks):
        parser.error("Task IDs must be unique.")
    args.plugin_version = json.loads((args.plugin / ".claude-plugin/plugin.json").read_text(encoding="utf-8-sig"))["version"]
    args.cli_version = subprocess.check_output([args.claude, "--version"], text=True).strip()
    revision = subprocess.run(["git", "-C", str(args.workspace), "rev-parse", "HEAD"], capture_output=True, text=True)
    args.revision = args.source_revision or (revision.stdout.strip() if revision.returncode == 0 else "unversioned-snapshot")
    args.output.mkdir(parents=True, exist_ok=False)
    schedule = make_schedule(tasks, args.repeats, args.seed, args.order, args.arms)
    (args.output / "experiment.json").write_text(json.dumps({
        "model": args.model, "provider_host": urlparse(os.environ.get("ANTHROPIC_BASE_URL", "https://api.anthropic.com")).hostname,
        "effort": args.effort, "cli_version": args.cli_version, "plugin_version": args.plugin_version,
        "repeats": args.repeats, "seed": args.seed, "order": args.order, "workers": args.workers,
        "restricted": args.restricted, "timeout_seconds": args.timeout, "reported_budget_per_run_usd": args.budget,
        "schedule": [{"index": i, "task_id": task["id"], "arm": arm, "repeat": repeat}
                     for i, (task, arm, repeat) in enumerate(schedule, 1)]}, indent=2), encoding="utf-8")
    rows = []
    phases = [[(i, entry) for i, entry in enumerate(schedule, 1) if entry[1] == arm] for arm in args.arms] if args.order == "baseline-first" else [list(enumerate(schedule, 1))]
    def execute(item):
        index, (task, arm, repeat) = item
        print(f"Run {index}/{len(schedule)}: {task['id']} {arm} repeat {repeat}", flush=True)
        row = run_one(args, task, arm, repeat, index)
        print(f"Finished {index}: {row['outcome']} {row['elapsed_ms']} ms", flush=True)
        return row
    for phase in phases:
        with ThreadPoolExecutor(max_workers=args.workers) as pool:
            for row in pool.map(execute, phase):
                rows.append(row)
                with (args.output / "metrics.csv").open("w", newline="", encoding="utf-8") as stream:
                    writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
                    writer.writeheader()
                    writer.writerows(rows)
    print(f"Aggregate metrics saved to {args.output}. Accuracy/review columns remain unscored.")


if __name__ == "__main__":
    main()
