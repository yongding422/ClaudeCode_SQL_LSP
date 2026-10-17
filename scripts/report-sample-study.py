"""Join explicitly reviewed factual grades to retained paired client measurements."""
import argparse
import csv
import hashlib
import json
from pathlib import Path
from statistics import mean, median


def blocks(event):
    message = event.get("message", {})
    content = message.get("content", []) if isinstance(message, dict) else []
    return [block for block in content if isinstance(block, dict)] if isinstance(content, list) else []


def analysis_statuses(events):
    statuses = []
    for event in events:
        for block in blocks(event):
            if block.get("type") != "tool_result":
                continue
            content = block.get("content")
            texts = [content] if isinstance(content, str) else [item.get("text", "") for item in content
                    if isinstance(item, dict) and item.get("type") == "text"] if isinstance(content, list) else []
            for text in texts:
                try:
                    payload = json.loads(text)
                except (ValueError, TypeError):
                    continue
                if isinstance(payload, dict) and isinstance(payload.get("report"), dict):
                    statuses.append(payload["report"].get("status", "unknown"))
                    break
    return statuses


def input_total(row):
    values = [row.get(key) for key in ("input_tokens", "cache_read_tokens", "cache_write_tokens")]
    return sum(values) if all(isinstance(value, (int, float)) for value in values) else None


def assess(row, grade, task):
    scores = grade["criteria"]
    if len(scores) != len(task["criteria"]) or any(isinstance(score, bool) or score not in (0, .5, 1) for score in scores):
        raise ValueError(f"Invalid criterion scores for {row['task_id']}")
    if isinstance(grade["citations"], bool) or grade["citations"] not in (0, .5, 1) or not isinstance(grade["material_unsupported_claim"], bool) or not grade.get("notes"):
        raise ValueError("Each grade requires citation score, explicit unsupported flag and review notes.")
    success = row["outcome"] == "success" and row["exit_code"] == 0
    score = mean(scores) if success else 0
    citation = grade["citations"] if success else 0
    return {**row, "factual_score": score, "reviewed_criteria": scores, "citation_score": citation,
            "material_unsupported_claim": grade["material_unsupported_claim"], "review_notes": grade["notes"],
            "strict_pass": success and score == 1 and citation == 1 and not grade["material_unsupported_claim"],
            "reported_total_input_tokens": input_total(row)}


def aggregate(rows):
    result = {"runs": len(rows), "successful_client_results": sum(row["outcome"] == "success" and row["exit_code"] == 0 for row in rows),
              "strict_passes": sum(row["strict_pass"] for row in rows),
              "mean_factual_score": mean(row["factual_score"] for row in rows),
              "mean_citation_score": mean(row["citation_score"] for row in rows),
              "mean_elapsed_ms": mean(row["elapsed_ms"] for row in rows),
              "median_elapsed_ms": median(row["elapsed_ms"] for row in rows),
              "tool_failures": sum(row["tool_failures"] for row in rows),
              "oversized_tool_results": sum(row["oversized_tool_results"] for row in rows),
              "runs_with_mcp_calls": sum(row["mcp_calls"] > 0 for row in rows),
              "runs_with_analysis_reports": sum(row["analysis_reports"] > 0 for row in rows),
              "runs_with_supported_analysis": sum(any(status in ("Resolved", "Partial") for status in row.get("analysis_statuses", [])) for row in rows)}
    for key in ("input_tokens", "output_tokens", "cache_read_tokens", "cache_write_tokens", "reported_total_input_tokens", "reported_cost_usd"):
        known = [row[key] for row in rows if row.get(key) is not None]
        result[key + "_known_runs"] = len(known)
        result[key + "_sum"] = sum(known) if known else None
        result[key + "_mean"] = mean(known) if known else None
    for tool in ("read", "grep", "glob", "mcp", "lsp", "skill", "other"):
        result[tool + "_calls"] = sum(row[tool + "_calls"] for row in rows)
    return result


def report(study, grades_path):
    manifest = json.loads((study / "manifest.json").read_text(encoding="utf-8"))
    for file in manifest["files"]:
        path = study / "sql-snapshot" / file["path"]
        if hashlib.sha256(path.read_bytes()).hexdigest() != file["sha256"]:
            raise ValueError("Frozen SQL snapshot changed: " + file["path"])
    for filename in ("rubric", "tasks"):
        actual = hashlib.sha256((study / (filename + ".json")).read_bytes()).hexdigest()
        if actual != manifest[filename + "_sha256"]:
            raise ValueError("Frozen input hash mismatch: " + filename)
    suite = json.loads((study / "rubric.json").read_text(encoding="utf-8"))
    tasks = {task["id"]: task for task in suite["tasks"]}
    grades = json.loads(grades_path.read_text(encoding="utf-8"))
    experiment = json.loads((study / "runs/experiment.json").read_text(encoding="utf-8"))
    rows, audit = [], []
    for entry in experiment["schedule"]:
        folder = study / "runs" / f"run-{entry['index']:04d}"
        row = json.loads((folder / "metrics.json").read_text(encoding="utf-8"))
        if (row["task_id"], row["arm"], row["repeat"]) != (entry["task_id"], entry["arm"], entry["repeat"]):
            raise ValueError("Schedule mismatch")
        if row["model_observed"] != experiment["model"] or row["repo_revision"] != manifest["source_revision"]:
            raise ValueError("Observed model/source revision mismatch")
        if row["sql_files"] != len(manifest["files"]):
            raise ValueError("Client SQL file count differs from the frozen input inventory")
        events = [json.loads(line) for line in (folder / "session.jsonl").read_text(encoding="utf-8").splitlines()]
        initial = next((event for event in events if event.get("type") == "system" and event.get("subtype") == "init"), {})
        calls = [block for event in events for block in blocks(event) if block.get("type") == "tool_use"]
        if row["arm"] == "grep" and (initial.get("mcp_servers") or any(block["name"] not in ("Read", "Grep", "Glob") for block in calls)):
            raise ValueError("Baseline isolation violated")
        if row["arm"] == "plugin" and not any(server.get("status") == "connected" and "sqlscriptdom-analysis" in server.get("name", "") for server in initial.get("mcp_servers", [])):
            audit.append({"run": folder.name, "issue": "SQL analysis server not connected at initialization"})
        if row["arm"] == "plugin" and any("sqlscriptdom-analysis" not in server.get("name", "") for server in initial.get("mcp_servers", [])):
            raise ValueError("Unexpected MCP server in plugin condition")
        analyzers = sum(block["name"].endswith("__analyze_procedure") for block in calls)
        discovery = sum(block["name"].endswith("__find_procedures") for block in calls)
        row.update(analyze_procedure_calls=analyzers, find_procedures_calls=discovery, analysis_statuses=analysis_statuses(events))
        rows.append(assess(row, grades[folder.name], tasks[row["task_id"]]))
    if set(grades) != {f"run-{entry['index']:04d}" for entry in experiment["schedule"]}:
        raise ValueError("Missing or extraneous grades")
    grouped = {arm: aggregate([row for row in rows if row["arm"] == arm]) for arm in ("grep", "plugin")}
    comparisons = {}
    for field in ("mean_factual_score", "mean_elapsed_ms", "reported_total_input_tokens_mean", "output_tokens_mean", "reported_cost_usd_sum"):
        baseline, treatment = grouped["grep"][field], grouped["plugin"][field]
        comparisons[field + "_plugin_change_percent"] = 100 * (treatment / baseline - 1) if baseline and treatment is not None else None
    result = {"experiment": experiment, "corpus_sha256": manifest["corpus_sha256"], "grading": suite["grading"], "comparisons": comparisons,
              "grades_sha256": hashlib.sha256(grades_path.read_bytes()).hexdigest(), "audit": audit, "summary": grouped, "runs": rows}
    (study / "reviewed-results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    with (study / "reviewed-results.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    lines = ["# SQL Server Samples: measured results", "", "Model: `" + experiment["model"] + "` through `" + experiment["provider_host"] + "`; plugin " + experiment["plugin_version"] + ". " +
             f"{len(tasks)} questions × {experiment['repeats']} repeats × 2 conditions; order={experiment['order']}. " +
             f"{experiment['workers']} concurrent client(s) within each phase; fresh SQL-only workspaces. No SQL was executed.", "",
             "| Metric | Read/Grep/Glob | SQL plugin + file tools |", "|---|---:|---:|"]
    metrics = [("Successful sessions", "successful_client_results", "{}"), ("Strict passing answers", "strict_passes", "{}"),
               ("Mean factual score", "mean_factual_score", "{:.1%}"), ("Mean citation score", "mean_citation_score", "{:.1%}"),
               ("Mean time (seconds)", "mean_elapsed_ms", "{:.1f}"), ("Median time (seconds)", "median_elapsed_ms", "{:.1f}"),
               ("Mean reported uncached input tokens", "input_tokens_mean", "{:,.0f}"),
               ("Mean reported total input tokens (including cache)", "reported_total_input_tokens_mean", "{:,.0f}"),
               ("Mean reported output tokens", "output_tokens_mean", "{:,.0f}"),
               ("Client-reported cost total (USD)", "reported_cost_usd_sum", "{:.4f}"),
               ("Tool failures", "tool_failures", "{}"), ("Oversized tool results", "oversized_tool_results", "{}"),
               ("Sessions invoking SQL MCP", "runs_with_mcp_calls", "{}"), ("Sessions with an inline analysis report", "runs_with_analysis_reports", "{}")]
    metrics.append(("Sessions with Resolved/Partial analysis (not Unresolved)", "runs_with_supported_analysis", "{}"))
    for label, key, formatting in metrics:
        values = [grouped[arm][key] for arm in ("grep", "plugin")]
        if key.endswith("elapsed_ms"):
            values = [value / 1000 for value in values]
        lines.append("| " + label + " | " + " | ".join(formatting.format(value) if value is not None else "unknown" for value in values) + " |")
    lines += ["", "Total input is the sum of reported input, cache-read and cache-write tokens across client requests. It is not unique source tokens. Reported costs use the client's accounting, not a verified DeepSeek invoice.", "",
              "## Per-question results", "", "Scores average the two repeats. Tokens include cached input; time includes client/MCP startup and tool failures. All assigned runs stay in the denominator.", "",
              "| Question | Grep facts | Plugin facts | Grep seconds | Plugin seconds | Grep input tokens | Plugin input tokens |", "|---|---:|---:|---:|---:|---:|---:|"]
    for task_id in tasks:
        arms = [aggregate([row for row in rows if row["task_id"] == task_id and row["arm"] == arm]) for arm in ("grep", "plugin")]
        lines.append(f"| {task_id} | {arms[0]['mean_factual_score']:.1%} | {arms[1]['mean_factual_score']:.1%} | {arms[0]['mean_elapsed_ms']/1000:.1f} | {arms[1]['mean_elapsed_ms']/1000:.1f} | {arms[0]['reported_total_input_tokens_mean']:,.0f} | {arms[1]['reported_total_input_tokens_mean']:,.0f} |")
    lines += ["", "## Review and limitations", "", suite["grading"], "",
              "The implementing agent reviewed the answers against the pre-run rubric and source. This is not independent human review. Baseline-first order, uncontrolled OS/provider caches and load, related procedures, selected questions and two repeats limit generalization. This measures a cold, instructed plugin-assisted workflow, not a warm persisted-cache workflow or a separate LSP-versus-MCP/codebase-memory experiment.", "",
              "Frozen corpus SHA256: `" + manifest["corpus_sha256"] + "`. Source revision: `" + manifest["source_revision"] + "`. Full questions/rubric, raw transcripts, answers, ungraded metrics and explicit review notes are retained alongside this report."]
    baseline, treatment = grouped["grep"], grouped["plugin"]
    if (treatment["mean_elapsed_ms"] > baseline["mean_elapsed_ms"] and
            treatment["reported_total_input_tokens_mean"] is not None and baseline["reported_total_input_tokens_mean"] is not None and
            treatment["reported_total_input_tokens_mean"] > baseline["reported_total_input_tokens_mean"] and
            treatment["mean_factual_score"] <= baseline["mean_factual_score"]):
        lines[2:2] = ["**On this suite, the released plugin was slower, used more reported input tokens, and did not improve overall factual coverage.**", ""]
    if (study / "findings.md").is_file():
        lines += ["", (study / "findings.md").read_text(encoding="utf-8")]
    (study / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(json.dumps(grouped, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--study", type=Path, required=True)
    parser.add_argument("--grades", type=Path, required=True)
    args = parser.parse_args()
    report(args.study.resolve(), args.grades.resolve())
