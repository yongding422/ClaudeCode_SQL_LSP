"""Separately labeled warm-index experiment; original cold runner/artifacts stay unchanged."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import csv
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
from statistics import mean
from types import SimpleNamespace

import importlib.util

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("rerun", HERE / "rerun-sample-study.py")
rerun = importlib.util.module_from_spec(spec)
spec.loader.exec_module(rerun)
evaluation = rerun.module("evaluate-understanding")
review = rerun.module("report-sample-study")
from warm_mcp_relay import WarmBackend

MODE = "prewarmed-native-index; fresh-model-client; authenticated-stdio-relay"


def environment():
    env = os.environ.copy()
    for key in ("SQLLSP_MCP_CONFIG", "SQLLSP_ANALYSIS_CACHE_DIR", "SQLLSP_METRICS_DIR"):
        env.pop(key, None)
    env["ENABLE_LSP_TOOL"] = "1"
    return env


def verify(study, plan):
    rerun.verify_inputs(study, plan)
    if plan["mode"] != MODE:
        raise ValueError("Not a prepared warm-index study")
    for name, sha in plan["warm_harness_sha256"].items():
        if rerun.digest(HERE / name) != sha:
            raise ValueError("Warm harness changed after preparation")


def prepare(args):
    args.workflow = "prescribed"
    args.suite = rerun.SUITE
    rerun.prepare(args)
    path = args.output / "rerun-plan.json"
    plan = rerun.read(path)
    plan.update(mode=MODE, warm_harness_sha256={name: rerun.digest(HERE / name) for name in
        ("warm-sample-study.py", "warm_mcp_relay.py", "report-sample-study.py")},
        warmup="Both arms read all frozen SQL bytes; plugin additionally initializes one native backend with two neutral discovery calls. No warmup results enter model context.",
        accounting="Timed question and warmup-inclusive times reported separately; model clients remain fresh, saved caches disabled.")
    path.write_text(json.dumps(plan, indent=2) + "\n", encoding="utf-8")
    print("Prepared separate warm-index mode; no model calls. Not a historical cold replication.")


def client_command(plan, arm, endpoint=None):
    tools = "Read,Grep,Glob" if arm == "grep" else "Read,Grep,Glob,LSP,Skill,ToolSearch"
    allowed = tools
    servers = {}
    if arm == "plugin":
        name = plan["candidate"]["name"]
        prefix = f"mcp__plugin_{name}_sqlscriptdom-analysis__"
        allowed += "," + prefix + "find_procedures," + prefix + "analyze_procedure"
        servers[f"plugin:{name}:sqlscriptdom-analysis"] = {"command": sys.executable,
            "args": [str(HERE / "warm_mcp_relay.py"), "--connect", str(endpoint)]}
    command = [plan["client"]["executable"], "--print", "--verbose", "--output-format", "stream-json",
        "--model", plan["model"], "--effort", plan["effort"], "--setting-sources", "", "--settings", '{"disableAllHooks":true}',
        "--strict-mcp-config", "--mcp-config", json.dumps({"mcpServers": servers}), "--no-session-persistence",
        "--permission-mode", "dontAsk", "--tools", tools, "--allowedTools", allowed,
        "--max-budget-usd", str(plan["budget"]), "--restricted"]
    command += ["--plugin-dir", plan["candidate"]["path"]] if arm == "plugin" else ["--disable-slash-commands"]
    return command


def row_for(plan, task, arm, repeat, index, files, elapsed, exit_code, events, malformed, warmup):
    metrics = evaluation.summarize(events)
    metrics["malformed_events"] += malformed
    if exit_code != 0 and metrics["outcome"] == "success":
        metrics["outcome"] = "process-error"
    if arm == "grep" and any(metrics[x + "_calls"] for x in ("lsp", "mcp", "skill", "other")):
        metrics["outcome"] = "baseline-tool-violation"
    return {"task_id": task["id"], "arm": arm, "repeat": repeat, "order": index, "model": plan["model"],
        "effort": plan["effort"], "repo_revision": plan["source_revision"], "cli_version": plan["client"]["version"],
        "plugin_version": plan["candidate"]["version"] if arm == "plugin" else None,
        "start_condition": MODE if arm == "plugin" else "SQL-bytes-preloaded; fresh-model-client; OS/provider caches uncontrolled",
        "sql_files": files, "elapsed_ms": round(elapsed), "exit_code": exit_code, **metrics,
        "warmup_ms": round(warmup["elapsed_ms"], 3), "warmup_inclusive_ms": round(elapsed + warmup["elapsed_ms"], 3),
        "warm_backend_verified": warmup.get("verified"), "dependency_precision": None, "dependency_recall": None,
        "behavior_score": None, "citation_score": None, "unsupported_handling_score": None, "reviewer_corrections": None}


def run_one(study, plan, task, arm, repeat, index):
    destination = study / "runs" / f"run-{index:04d}"
    destination.mkdir()
    backend = None
    warmup = {"elapsed_ms": 0, "verified": None}
    events, raw, stderr, malformed, exit_code = [], "", "", 0, -1
    elapsed = 0
    with tempfile.TemporaryDirectory(prefix="sql-warm-question-") as temporary:
        workspace = Path(temporary).resolve()
        files = evaluation.copy_sql(study / "sql-snapshot", workspace)
        env = environment()
        env["CLAUDE_CONFIG_DIR"] = str(destination / "isolated-config")
        started = time.perf_counter()
        try:
            # Identical no-model page-cache preload in both arms; no filenames/results enter the prompt.
            for file in sorted(workspace.rglob("*.sql")):
                file.read_bytes()
            preload_ms = (time.perf_counter() - started) * 1000
            if arm == "plugin":
                binary = Path(plan["candidate"]["path"]) / "mcp/sqlscriptdom-mcp.exe"
                if not binary.exists():
                    binary = binary.with_suffix("")
                backend = WarmBackend([str(binary)], workspace, files, env)
            warmup = {"elapsed_ms": (time.perf_counter() - started) * 1000, "sql_byte_preload_ms": preload_ms,
                "native": backend.evidence() if backend else None, "verified": True if backend else None}
            command = client_command(plan, arm, backend.endpoint if backend else None)
            workflow = ("Use Read/Grep/Glob to investigate the SQL source. " if arm == "grep" else
                "Use the SQL plugin's find_procedures and analyze_procedure tools for relevant modules first; use Read/Grep/Glob to verify or fill gaps. ")
            command += [task["question"] + "\n" + workflow +
                "Answer in at most 450 words with file:line citations, direct versus transitive effects where relevant, "
                "and explicit unknowns. Do not execute SQL or run shell commands. Report tool failures honestly."]
            measured_start = time.perf_counter()
            with subprocess.Popen(command, cwd=workspace, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                    text=True, encoding="utf-8", errors="replace", start_new_session=os.name != "nt",
                    creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0) as process:
                timed_out = False
                try:
                    raw, stderr = process.communicate(timeout=plan["timeout"])
                except subprocess.TimeoutExpired:
                    timed_out = True
                    if os.name == "nt":
                        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True, check=False)
                    else:
                        os.killpg(process.pid, signal.SIGKILL)
                    raw, stderr = process.communicate()
                exit_code = process.returncode
            elapsed = (time.perf_counter() - measured_start) * 1000
            for line in raw.splitlines():
                try:
                    events.append(json.loads(line))
                except ValueError:
                    malformed += 1
            if backend:
                warmup["native"] = backend.evidence()
                proof = warmup["native"]
                warmup["verified"] = proof["same_backend_alive_after_client"] and proof["relay_error"] is None and all(
                    item.get("timings", {}).get("coldStart") is False for item in proof["measured_calls"] if item.get("timings"))
            row = row_for(plan, task, arm, repeat, index, files, elapsed, exit_code, events, malformed, warmup)
            if timed_out:
                row["outcome"] = "timeout"
            if backend and not warmup["verified"]:
                row["outcome"] = "warmth-verification-failed"
        except Exception as error:
            warmup["elapsed_ms"] = (time.perf_counter() - started) * 1000
            warmup["verified"] = False if arm == "plugin" else None
            warmup["error_type"] = type(error).__name__
            row = row_for(plan, task, arm, repeat, index, files, elapsed, exit_code, events, malformed, warmup)
            row["outcome"] = "warmup-or-client-error"
        finally:
            if backend:
                backend.close()
        (destination / "session.jsonl").write_text(raw, encoding="utf-8")
        (destination / "stderr.log").write_text(stderr, encoding="utf-8")
        final = next((e for e in reversed(events) if e.get("type") == "result"), {})
        (destination / "answer.txt").write_text(str(final.get("result", "")), encoding="utf-8")
        (destination / "warmup.json").write_text(json.dumps(warmup, indent=2), encoding="utf-8")
        (destination / "metrics.json").write_text(json.dumps(row, indent=2), encoding="utf-8")
        return row


def run(args):
    study = args.study.resolve()
    plan = rerun.read(study / "rerun-plan.json")
    verify(study, plan)
    if args.dry_run:
        print(json.dumps({"mode": MODE, "sessions": plan["runs"], "model_calls": 0, "prewarm": plan["warmup"]}, indent=2))
        return
    current = rerun.client(plan["client"]["executable"])
    if any(current[key] != plan["client"][key] for key in ("version", "provider_host")) or not current["environment_credentials_present"]:
        raise ValueError("Client/provider/auth prerequisites changed")
    plan["source_revision"] = rerun.read(study / "manifest.json")["source_revision"]
    tasks = rerun.read(study / "tasks.json")
    schedule = evaluation.make_schedule(tasks, plan["repeats"], plan["seed"], plan["order"])
    output = study / "runs"
    output.mkdir(exist_ok=False)
    experiment = {"model": plan["model"], "provider_host": current["provider_host"], "effort": plan["effort"],
        "cli_version": current["version"], "plugin_version": plan["candidate"]["version"], "repeats": plan["repeats"],
        "seed": plan["seed"], "order": plan["order"], "workers": plan["workers"], "workflow": plan["workflow"],
        "restricted": True, "timeout_seconds": plan["timeout"], "reported_budget_per_run_usd": plan["budget"], "cache_condition": MODE,
        "schedule": [{"index": i, "task_id": task["id"], "arm": arm, "repeat": repeat} for i, (task, arm, repeat) in enumerate(schedule, 1)]}
    (output / "experiment.json").write_text(json.dumps(experiment, indent=2), encoding="utf-8")
    rows = []
    def execute(item):
        index, (task, arm, repeat) = item
        print(f"Run {index}/{len(schedule)}: {task['id']} {arm} repeat {repeat}", flush=True)
        row = run_one(study, plan, task, arm, repeat, index)
        print(f"Finished {index}: {row['outcome']} question={row['elapsed_ms']}ms warmup={row['warmup_ms']}ms verified={row['warm_backend_verified']}", flush=True)
        return row
    for arm in ("grep", "plugin"):
        with ThreadPoolExecutor(max_workers=plan["workers"]) as pool:
            for row in pool.map(execute, [(i, x) for i, x in enumerate(schedule, 1) if x[1] == arm]):
                rows.append(row)
                with (output / "metrics.csv").open("w", newline="", encoding="utf-8") as stream:
                    writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
                    writer.writeheader()
                    writer.writerows(rows)
    print("All assigned sessions retained. Review new answers before interpreting performance.")


def report(args):
    study = args.study.resolve()
    plan = rerun.read(study / "rerun-plan.json")
    verify(study, plan)
    if rerun.digest(rerun.BASELINE) != plan["historical_baseline_sha256"]:
        raise ValueError("Historical baseline changed")
    manifest = rerun.read(study / "manifest.json")
    experiment = rerun.read(study / "runs/experiment.json")
    tasks = {t["id"]: t for t in rerun.read(study / "rubric.json")["tasks"]}
    grades = rerun.read(args.grades)
    rows, audit = [], []
    for entry in experiment["schedule"]:
        folder = study / "runs" / f"run-{entry['index']:04d}"
        row = rerun.read(folder / "metrics.json")
        if (row["task_id"], row["arm"], row["repeat"]) != (entry["task_id"], entry["arm"], entry["repeat"]):
            raise ValueError("Schedule mismatch")
        if row["sql_files"] != len(manifest["files"]) or row["repo_revision"] != manifest["source_revision"]:
            raise ValueError("Frozen source differs")
        if row["model_observed"] != experiment["model"] and row["outcome"] == "success":
            raise ValueError("Observed model differs")
        events = [json.loads(line) for line in (folder / "session.jsonl").read_text(encoding="utf-8").splitlines()]
        initial = next((e for e in events if e.get("type") == "system" and e.get("subtype") == "init"), {})
        calls = [b for e in events for b in review.blocks(e) if b.get("type") == "tool_use"]
        servers = initial.get("mcp_servers", [])
        if row["arm"] == "grep" and (servers or any(b["name"] not in ("Read", "Grep", "Glob") for b in calls)):
            raise ValueError("Baseline isolation violated")
        if row["arm"] == "plugin":
            if any("sqlscriptdom-analysis" not in s.get("name", "") for s in servers):
                raise ValueError("Unexpected MCP server")
            if not any(s.get("status") == "connected" for s in servers):
                audit.append({"run": folder.name, "issue": "SQL MCP not connected"})
            warmup = rerun.read(folder / "warmup.json")
            native = warmup.get("native") or {}
            if not row["warm_backend_verified"] or any(x.get("backend_pid") != native.get("backend_pid") for x in native.get("measured_calls", [])):
                audit.append({"run": folder.name, "issue": "Warmth not verified"})
        row.update(analysis_statuses=review.analysis_statuses(events),
            analyze_procedure_calls=sum(b["name"].endswith("__analyze_procedure") for b in calls),
            find_procedures_calls=sum(b["name"].endswith("__find_procedures") for b in calls))
        rows.append(review.assess(row, grades[folder.name], tasks[row["task_id"]]))
    if set(grades) != {f"run-{x['index']:04d}" for x in experiment["schedule"]}:
        raise ValueError("Missing or extraneous grades")
    grouped = {arm: review.aggregate([r for r in rows if r["arm"] == arm]) for arm in ("grep", "plugin")}
    for arm in grouped:
        arm_rows = [r for r in rows if r["arm"] == arm]
        grouped[arm].update(mean_warmup_ms=mean(r["warmup_ms"] for r in arm_rows),
            mean_warmup_inclusive_ms=mean(r["warmup_inclusive_ms"] for r in arm_rows))
    result = {"experiment": experiment, "corpus_sha256": manifest["corpus_sha256"], "summary": grouped, "runs": rows,
        "audit": audit, "grades_sha256": rerun.digest(args.grades), "grading": rerun.read(study / "rubric.json")["grading"]}
    (study / "reviewed-results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    with (study / "reviewed-results.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0])); writer.writeheader(); writer.writerows(rows)
    lines = ["# SQL Server Samples: prewarmed native index", "", "Fresh model clients; same owned native MCP backend prewarmed before each measured question. Both arms preload SQL bytes. Warmup replies never enter model context. Saved caches disabled; SQL never executed.", "",
        "| Metric | Grep + reading | Prewarmed plugin + file tools |", "|---|---:|---:|"]
    for label, key, scale, fmt in [("Successful sessions", "successful_client_results", 1, ".0f"), ("Strict passes", "strict_passes", 1, ".0f"),
        ("Factual score", "mean_factual_score", 1, ".1%"), ("Citation score", "mean_citation_score", 1, ".1%"),
        ("Measured question seconds", "mean_elapsed_ms", .001, ".2f"), ("Warmup seconds", "mean_warmup_ms", .001, ".2f"),
        ("Warmup-inclusive seconds", "mean_warmup_inclusive_ms", .001, ".2f"),
        ("Total reported input tokens incl. cache", "reported_total_input_tokens_mean", 1, ",.0f"),
        ("Uncached input tokens", "input_tokens_mean", 1, ",.0f"), ("Cache-read tokens", "cache_read_tokens_mean", 1, ",.0f"),
        ("Output tokens", "output_tokens_mean", 1, ",.0f"), ("Reported cost USD total", "reported_cost_usd_sum", 1, ".4f"),
        ("Tool errors", "tool_failures", 1, ".0f"), ("Overflows", "oversized_tool_results", 1, ".0f"),
        ("Sessions consuming supported body analysis", "runs_with_supported_analysis", 1, ".0f")]:
        values = [grouped[a][key] for a in ("grep", "plugin")]
        lines.append("| " + label + " | " + " | ".join(format(v * scale, fmt) if v is not None else "unknown" for v in values) + " |")
    lines += ["", "## Per-question results", "", "| Question | Grep facts | Plugin facts | Grep seconds | Warm plugin seconds | Grep input | Plugin input |", "|---|---:|---:|---:|---:|---:|---:|"]
    for task in tasks:
        values = [review.aggregate([r for r in rows if r["task_id"] == task and r["arm"] == a]) for a in ("grep", "plugin")]
        a,b=values
        token_values=[str(round(v['reported_total_input_tokens_mean'])) if v['reported_total_input_tokens_mean'] is not None else 'unknown' for v in values]
        lines.append(f"| {task} | {a['mean_factual_score']:.1%} | {b['mean_factual_score']:.1%} | {a['mean_elapsed_ms']/1000:.2f} | {b['mean_elapsed_ms']/1000:.2f} | {' | '.join(token_values)} |")
    lines += ["", "All assigned outcomes/fallbacks remain in their arm. Review is implementing-agent, not independent/blinded; two repeats on selected related questions, baseline first, OS/provider caches/load uncontrolled. Costs are client-reported, not invoices. Startup/copy exclusions: SQL snapshot copying excluded in both arms; file preloading and native warmup excluded only from measured-question time and included in warmup-inclusive time. No previous answer/context crosses questions. The relay adds transport overhead and replaces fresh MCP initialization with the exact prewarmed backend's supported initialization response; requested/negotiated protocol versions are retained. Candidate native binaries/instructions/tools unchanged.",
        "", "This is a warm-server ablation, not a cold replication or a multi-turn conversation experiment. It does not measure LSP-specific benefit or codebase-memory. Historical comparisons have explicit cache-condition and relay-transport mismatches."]
    if (study / "findings.md").exists(): lines += ["", (study / "findings.md").read_text(encoding="utf-8")]
    (study / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    historical = rerun.compare(rerun.read(rerun.BASELINE), {**result, "comparisons": {}}, manifest, plan)
    historical['matching_dimensions'].update(cache_condition=False, mcp_transport=False)
    historical['mismatches'] += ['cache_condition','mcp_transport']
    historical['all_recorded_dimensions_match'] = False
    (study / "baseline-comparison.json").write_text(json.dumps(historical, indent=2), encoding="utf-8")
    print(json.dumps(grouped, indent=2))


def pilot(args):
    candidate = rerun.candidate(args.plugin)
    with tempfile.TemporaryDirectory(prefix="sql-warm-pilot-") as temporary:
        workspace = Path(temporary)
        files = evaluation.copy_sql(args.source, workspace)
        binary = Path(candidate['path']) / ('mcp/sqlscriptdom-mcp.exe' if os.name == 'nt' else 'mcp/sqlscriptdom-mcp')
        with WarmBackend([str(binary)], workspace, files, environment()) as backend:
            command = [sys.executable, str(HERE / 'warm_mcp_relay.py'), '--connect', str(backend.endpoint)]
            with subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE) as relay:
                from warm_mcp_relay import send,frame,payload
                send(relay.stdin, {'jsonrpc':'2.0','id':1,'method':'initialize','params':{'protocolVersion':backend.initial['protocolVersion'],'capabilities':{},'clientInfo':{'name':'relay-pilot','version':'1'}}})
                assert frame(relay.stdout)['result']['serverInfo']['name']=='sqlscriptdom-analysis'
                send(relay.stdin, {'jsonrpc':'2.0','method':'notifications/initialized'})
                send(relay.stdin, {'jsonrpc':'2.0','id':2,'method':'tools/list'})
                assert 'find_procedures' in [x['name'] for x in frame(relay.stdout)['result']['tools']]
                send(relay.stdin, {'jsonrpc':'2.0','id':3,'method':'tools/call','params':{'name':'find_procedures','arguments':{'query':'InsertCustomerOrders','maxResults':1}}})
                data=payload(frame(relay.stdout));assert data['timings']['coldStart'] is False
                relay.stdin.close(); relay.wait(timeout=10)
            evidence=backend.evidence()
            args.output.write_text(json.dumps(evidence,indent=2),encoding='utf-8')
            assert evidence['same_backend_alive_after_client'] and evidence['relay_error'] is None, str(evidence)
            print(json.dumps({'model_calls':0,'backend_pid':backend.pid,'indexed_documents':data['indexedDocuments'],'measured_cold_start':data['timings']['coldStart'],'relay_verified':True}))


if __name__ == "__main__":
    parser=argparse.ArgumentParser(description=__doc__);sub=parser.add_subparsers(dest='action',required=True)
    p=sub.add_parser('prepare');p.add_argument('--source',type=Path,required=True);p.add_argument('--plugin',type=Path,required=True);p.add_argument('--model',required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--claude');p.add_argument('--repeats',type=int,default=2);p.add_argument('--workers',type=int,default=2);p.add_argument('--timeout',type=int,default=180);p.add_argument('--budget',type=float,default=.5)
    p=sub.add_parser('run');p.add_argument('--study',type=Path,required=True);p.add_argument('--dry-run',action='store_true')
    p=sub.add_parser('grade-template');p.add_argument('--study',type=Path,required=True)
    p=sub.add_parser('report');p.add_argument('--study',type=Path,required=True);p.add_argument('--grades',type=Path,required=True)
    p=sub.add_parser('pilot');p.add_argument('--source',type=Path,required=True);p.add_argument('--plugin',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    {'prepare':prepare,'run':run,'grade-template':rerun.grade_template,'report':report,'pilot':pilot}[args.action](args)
