"""Freeze acceptance rubrics, prepare anonymous review packets, and report explicit reviews.

No model, database, keyword judge, or automatic user-satisfaction inference is used.
Historical studies and their factual grades are read-only inputs.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import secrets
from statistics import mean


DECISIONS = ("accept_as_written", "minor_edits", "major_correction", "reject")
REVIEWER_TYPES = ("implementing_agent", "independent_agent", "implementing_human", "independent_human")
ROOT = Path(__file__).resolve().parents[1]
DEFAULT_RUBRIC = ROOT / "docs/evaluation/sql-server-samples-acceptance-v1.json"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(path, value):
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2, allow_nan=False)
        stream.write("\n")


def text(value):
    return isinstance(value, str) and bool(value.strip())


def number(value):
    return type(value) in (int, float) and math.isfinite(value) and value >= 0


def safe_child(root, relative):
    path = (root / relative).resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError("Path escapes its frozen input directory")
    return path


def verify_study(study):
    manifest = read(study / "manifest.json")
    for name in ("rubric", "tasks"):
        if sha(study / f"{name}.json") != manifest[f"{name}_sha256"]:
            raise ValueError("Frozen study tasks/rubric changed")
    snapshot = study / "sql-snapshot"
    actual = {p.relative_to(snapshot).as_posix() for p in snapshot.rglob("*") if p.is_file()}
    if actual != {f["path"] for f in manifest["files"]}:
        raise ValueError("Frozen SQL inventory changed")
    for entry in manifest["files"]:
        if sha(safe_child(snapshot, entry["path"])) != entry["sha256"]:
            raise ValueError("Frozen SQL content changed")
    return manifest


def validate_rubric(rubric, suite, suite_hash):
    if rubric.get("format") != "sql-answer-acceptance-v1" or rubric.get("suite_sha256") != suite_hash:
        raise ValueError("Acceptance rubric must name the exact frozen suite hash")
    if rubric.get("source_revision") != suite.get("source_revision"):
        raise ValueError("Acceptance/source revision mismatch")
    tasks = {task["id"]: task for task in suite["tasks"]}
    ids = [task["task_id"] for task in rubric["tasks"]]
    if len(ids) != len(set(ids)) or set(ids) != set(tasks):
        raise ValueError("Acceptance tasks must match the entire cohort exactly")
    for task in rubric["tasks"]:
        if not text(task.get("developer_goal")):
            raise ValueError("Each task needs a developer goal")
        for key in ("essential_facts", "critical_errors", "required_evidence", "expected_uncertainty", "optional_detail"):
            values = task.get(key)
            if not isinstance(values, list) or (key != "optional_detail" and not values) or any(not text(v) for v in values):
                raise ValueError("Invalid acceptance checklist: " + key)


def freeze(study, rubric_path, output):
    study, output = study.resolve(), output.resolve()
    if output.is_relative_to(study) or study.is_relative_to(output):
        raise ValueError("Use a separate acceptance output directory outside the study")
    manifest = verify_study(study)
    rubric, suite = read(rubric_path), read(study / "rubric.json")
    # prepare-sample-study normalizes JSON when freezing; compare canonical suite identity.
    suite_hash = hashlib.sha256(json.dumps(suite, sort_keys=True).encode()).hexdigest()
    validate_rubric(rubric, suite, suite_hash)
    output.mkdir(parents=True, exist_ok=False)
    save(output / "rubric.json", rubric)
    save(output / "contract.json", {
        "format": "sql-answer-acceptance-contract-v1", "study": str(study),
        "prepared_utc": datetime.now(timezone.utc).isoformat(),
        "timing": "retrospective" if (study / "runs").exists() else "prospective",
        "manifest_sha256": sha(study / "manifest.json"), "rubric_sha256": sha(output / "rubric.json"),
        "source_revision": manifest["source_revision"], "corpus_sha256": manifest["corpus_sha256"],
        "implementation_sha256": sha(Path(__file__))})


def contract_inputs(output):
    contract = read(output / "contract.json")
    study = Path(contract["study"])
    if sha(study / "manifest.json") != contract["manifest_sha256"] or sha(output / "rubric.json") != contract["rubric_sha256"]:
        raise ValueError("Acceptance contract inputs changed")
    verify_study(study)
    return study, contract, read(output / "rubric.json")


def scheduled_inputs(study):
    experiment = read(study / "runs/experiment.json")
    tasks = {task["id"]: task for task in read(study / "rubric.json")["tasks"]}
    repeats = experiment["repeats"]
    if type(repeats) is not int or repeats < 1:
        raise ValueError("Invalid repeat count")
    expected = {(task, arm, repeat) for task in tasks for arm in ("grep", "plugin") for repeat in range(1, repeats + 1)}
    entries, seen, indices = [], set(), set()
    for entry in experiment["schedule"]:
        key = (entry["task_id"], entry["arm"], entry["repeat"])
        index = entry["index"]
        if key in seen or key not in expected or type(index) is not int or index < 1 or index in indices:
            raise ValueError("Duplicate or unexpected scheduled question/arm/repeat")
        seen.add(key)
        indices.add(index)
        folder = study / "runs" / f"run-{index:04d}"
        metrics, answer = folder / "metrics.json", folder / "answer.txt"
        if metrics.exists():
            row = read(metrics)
            if (row["task_id"], row["arm"], row["repeat"]) != key:
                raise ValueError("Metrics/schedule mismatch")
        else:
            row = {"task_id": key[0], "arm": key[1], "repeat": key[2], "outcome": "missing-result", "exit_code": None}
        body = answer.read_text(encoding="utf-8-sig") if answer.exists() else ""
        entries.append({"run": folder.name, "task_id": key[0], "arm": key[1], "repeat": key[2],
            "metrics_sha256": sha(metrics) if metrics.exists() else None,
            "answer_sha256": sha(answer) if answer.exists() else None,
            "row": row, "answer": body})
    if seen != expected:
        raise ValueError("Incomplete assigned schedule; missing runs must remain scheduled")
    return entries, tasks, experiment


def packet(output):
    study, contract, rubric = contract_inputs(output)
    entries, tasks, _ = scheduled_inputs(study)
    secrets.SystemRandom().shuffle(entries)
    reviewer = output / "reviewer"
    reviewer.mkdir(exist_ok=False)
    (reviewer / "answers").mkdir()
    mapping, template, feedback = {}, {}, {}
    acceptance = {task["task_id"]: task for task in rubric["tasks"]}
    for entry in entries:
        case_id = "answer-" + secrets.token_hex(8)
        answer_path = reviewer / "answers" / f"{case_id}.txt"
        answer_path.write_text(entry.pop("answer"), encoding="utf-8")
        entry["packet_answer_sha256"] = sha(answer_path)
        mapping[case_id] = entry
        task = tasks[entry["task_id"]]
        template[case_id] = {"task_id": task["id"], "essential_facts": [None] * len(acceptance[task["id"]]["essential_facts"]),
            "evidence_support": None, "uncertainty_handling": None, "relevance": None,
            "critical_errors": None, "material_unsupported_claims": None, "decision": None,
            "correction_minutes": None, "follow_up_questions": None, "notes": ""}
        feedback[case_id] = {"decision": None, "correction_minutes": None, "follow_up_questions": None, "notes": ""}
    save(reviewer / "questions.json", [{"id": task["id"], "question": task["question"], "evidence": task["evidence"]} for task in tasks.values()])
    save(reviewer / "rubric.json", rubric)
    save(reviewer / "reviews.template.json", {"reviewer": {"id": "", "type": None, "arm_labels_seen": None}, "answers": template})
    save(reviewer / "user-feedback.template.json", {"respondent": {"id": "", "type": "human_developer"}, "answers": feedback})
    (reviewer / "README.md").write_text(
        "# Anonymous answer review\n\nRead questions.json, rubric.json and every answers/*.txt. Check citations against "
        + str(study / "sql-snapshot") + ". Copy reviews.template.json to reviews.json and fill every score/checklist/decision; "
        "no scores are inferred. Scores are 0/0.5/1. Empty findings must be explicit []. Critical or unsupported findings need "
        "claim, explanation, evidence. Correction time and follow-ups may remain null when unmeasured. See the maintainer acceptance protocol.\n\n"
        "Do not inspect ../private, transcripts or measurement files until review is submitted. Arm names, timings and tool logs "
        "are withheld, but answer wording may reveal tools. Record arm_labels_seen honestly; prior implementing-agent knowledge "
        "is not independent review. Only a real developer supplies user-feedback.json. Missing feedback stays unknown.\n", encoding="utf-8")
    (output / "private").mkdir()
    save(output / "private/mapping.json", {"contract_sha256": sha(output / "contract.json"),
        "experiment_sha256": sha(study / "runs/experiment.json"), "answers": mapping,
        "review_material_sha256": {name: sha(reviewer / name) for name in ("questions.json", "rubric.json")}})


def validate_review(grade, task, successful):
    scores = grade.get("essential_facts")
    if not isinstance(scores, list) or len(scores) != len(task["essential_facts"]):
        raise ValueError("Essential fact score count differs from rubric")
    for score in scores + [grade.get(k) for k in ("evidence_support", "uncertainty_handling", "relevance")]:
        if type(score) not in (int, float) or score not in (0, .5, 1):
            raise ValueError("Review scores must be explicitly 0, 0.5 or 1")
    for key in ("critical_errors", "material_unsupported_claims"):
        findings = grade.get(key)
        if not isinstance(findings, list) or any(not isinstance(f, dict) or any(not text(f.get(k)) for k in ("claim", "explanation", "evidence")) for f in findings):
            raise ValueError("Findings require explicit lists and claim/explanation/evidence")
    decision = grade.get("decision")
    if decision not in DECISIONS or not text(grade.get("notes")):
        raise ValueError("Explicit acceptance decision and review notes required")
    for key in ("correction_minutes", "follow_up_questions"):
        value = grade.get(key)
        if value is not None and (not number(value) or (key == "follow_up_questions" and type(value) is not int)):
            raise ValueError("Correction effort must be nonnegative or unknown")
    issues = grade["critical_errors"] or grade["material_unsupported_claims"]
    if decision in ("accept_as_written", "minor_edits"):
        if not successful or any(score != 1 for score in scores) or issues or grade["uncertainty_handling"] != 1:
            raise ValueError("Acceptance contradicts failure, incomplete essential facts, or consequential findings")
        minimum = 1 if decision == "accept_as_written" else .5
        if grade["evidence_support"] < minimum or grade["relevance"] < minimum:
            raise ValueError("Acceptance requires adequate evidence and relevance")
    if not successful and decision != "reject":
        raise ValueError("Failed or missing answers must be rejected")


def aggregate(rows):
    count = len(rows)
    result = {"assigned": count, "successful_answers": sum(r["successful"] for r in rows),
        "reviewer_accept_as_written": sum(r["decision"] == "accept_as_written" for r in rows),
        "reviewer_acceptance_rate": mean(r["decision"] == "accept_as_written" for r in rows),
        "decision_counts": {d: sum(r["decision"] == d for r in rows) for d in DECISIONS},
        "answers_with_critical_errors": sum(bool(r["critical_errors"]) for r in rows),
        "critical_error_rate": mean(bool(r["critical_errors"]) for r in rows),
        "material_unsupported_claim_rate": mean(bool(r["material_unsupported_claims"]) for r in rows)}
    for key in ("essential_fact_coverage", "evidence_support", "uncertainty_handling", "relevance"):
        result["mean_" + key] = mean(r[key] if r["successful"] else 0 for r in rows)
    for key in ("correction_minutes", "follow_up_questions", "elapsed_ms", "total_input_tokens", "output_tokens"):
        known = [r[key] for r in rows if r.get(key) is not None]
        result[key] = {"observed": len(known), "unknown": count - len(known), "mean": mean(known) if known else None}
    observed = [r for r in rows if r["user_decision"] is not None]
    accepted = sum(r["user_decision"] == "accept_as_written" for r in observed)
    result["actual_user_acceptance"] = {"observed": len(observed), "unknown": count - len(observed), "accepted": accepted,
        "observed_rate": accepted / len(observed) if observed else None,
        "all_assigned_rate": accepted / count if len(observed) == count else None}
    for key in ("user_correction_minutes", "user_follow_up_questions"):
        known = [r[key] for r in rows if r.get(key) is not None]
        result[key] = {"observed": len(known), "unknown": count - len(known), "mean": mean(known) if known else None}
    return result


def report(output, reviews_path, destination, user_feedback_path=None):
    study, contract, rubric = contract_inputs(output)
    mapping_file = output / "private/mapping.json"
    mapping = read(mapping_file)
    if mapping["contract_sha256"] != sha(output / "contract.json") or mapping["experiment_sha256"] != sha(study / "runs/experiment.json"):
        raise ValueError("Review mapping/experiment changed")
    for name, digest in mapping["review_material_sha256"].items():
        if sha(safe_child(output / "reviewer", name)) != digest:
            raise ValueError("Reviewer questions or acceptance rubric changed")
    entries, _, experiment = scheduled_inputs(study)
    current = {entry["run"]: entry for entry in entries}
    mapped = mapping["answers"]
    if len(mapped) != len(current) or {entry["run"] for entry in mapped.values()} != set(current):
        raise ValueError("Missing or duplicate mapped answers")
    reviews = read(reviews_path)
    reviewer = reviews["reviewer"]
    if not text(reviewer.get("id")) or reviewer.get("type") not in REVIEWER_TYPES or type(reviewer.get("arm_labels_seen")) is not bool:
        raise ValueError("Reviewer identity/type and blinding disclosure required")
    if set(reviews["answers"]) != set(mapped):
        raise ValueError("Review every assigned answer; missing/extraneous reviews")
    if destination.resolve().is_relative_to(study.resolve()) or destination.resolve().is_relative_to(output.resolve()):
        raise ValueError("Write reports to a new separate directory; preserve study and review inputs")
    feedback = read(user_feedback_path) if user_feedback_path else None
    if feedback is not None:
        if feedback.get("respondent", {}).get("type") != "human_developer" or not text(feedback["respondent"].get("id")):
            raise ValueError("Actual user feedback needs a named human developer respondent")
        if set(feedback["answers"]) != set(mapped):
            raise ValueError("Feedback must retain the complete template with nulls for unobserved answers")
    tasks = {t["task_id"]: t for t in rubric["tasks"]}
    rows = []
    for case, entry in mapped.items():
        now = current[entry["run"]]
        for key in ("metrics_sha256", "answer_sha256", "task_id", "arm", "repeat"):
            if entry[key] != now[key]:
                raise ValueError("Answer/metrics/schedule changed since review packet creation")
        if sha(output / "reviewer/answers" / f"{case}.txt") != entry["packet_answer_sha256"]:
            raise ValueError("Anonymous answer was edited")
        grade = reviews["answers"][case]
        if grade.get("task_id") != entry["task_id"]:
            raise ValueError("Review task mismatch")
        row = now["row"]
        successful = row["outcome"] == "success" and row["exit_code"] == 0 and text(now["answer"])
        validate_review(grade, tasks[entry["task_id"]], successful)
        user = feedback["answers"][case] if feedback else {}
        if user.get("decision") is not None:
            if user["decision"] not in DECISIONS or not text(user.get("notes")) or (not successful and user["decision"] != "reject"):
                raise ValueError("Invalid actual user feedback")
        for key in ("correction_minutes", "follow_up_questions"):
            value = user.get(key)
            if value is not None and (user.get("decision") is None or not number(value) or (key == "follow_up_questions" and type(value) is not int)):
                raise ValueError("Invalid user correction effort")
        inputs = [row.get(k) for k in ("input_tokens", "cache_read_tokens", "cache_write_tokens")]
        rows.append({"answer_id": case, "task_id": entry["task_id"], "arm": entry["arm"], "repeat": entry["repeat"],
            **grade, "successful": successful, "essential_fact_coverage": mean(grade["essential_facts"]),
            "elapsed_ms": row.get("elapsed_ms"), "total_input_tokens": sum(inputs) if all(number(v) for v in inputs) else None,
            "output_tokens": row.get("output_tokens"), "user_decision": user.get("decision"), "user_notes": user.get("notes"),
            "user_correction_minutes": user.get("correction_minutes"), "user_follow_up_questions": user.get("follow_up_questions")})
    summary = {arm: aggregate([r for r in rows if r["arm"] == arm]) for arm in ("grep", "plugin")}
    per_task = {task: {arm: aggregate([r for r in rows if r["arm"] == arm and r["task_id"] == task]) for arm in summary} for task in tasks}
    pairs = {"both_accepted": 0, "grep_only_accepted": 0, "plugin_only_accepted": 0, "neither_accepted": 0}
    for task in tasks:
        for repeat in range(1, experiment["repeats"] + 1):
            accepted = {r["arm"]: r["decision"] == "accept_as_written" for r in rows if r["task_id"] == task and r["repeat"] == repeat}
            key = "both_accepted" if all(accepted.values()) else "grep_only_accepted" if accepted["grep"] else "plugin_only_accepted" if accepted["plugin"] else "neither_accepted"
            pairs[key] += 1
    destination.mkdir(parents=True, exist_ok=False)
    result = {"format": "sql-answer-acceptance-report-v1", "contract": contract, "reviewer": reviewer,
        "respondent": feedback["respondent"] if feedback else None, "summary": summary, "per_task": per_task,
        "paired_acceptance": pairs, "runs": rows, "reviews_sha256": sha(reviews_path),
        "user_feedback_sha256": sha(user_feedback_path) if user_feedback_path else None,
        "mapping_sha256": sha(mapping_file), "reporter_sha256": sha(Path(__file__)),
        "limitations": "Reviewer judgments are not automatically user acceptance. Labels withheld does not prove blinding. "
            "Related questions/repeats are not independent samples. No statistical superiority claim. "
            "Retrospective rubrics cannot be presented as predeclared. Missing effort/feedback/usage stays unknown."}
    save(destination / "acceptance-results.json", result)
    display = ["# Answer acceptance review", "", f"Rubric timing: {contract['timing']}. Reviewer: {reviewer['id']} ({reviewer['type']}); arm labels seen: {reviewer['arm_labels_seen']}.", "",
        "| Quality outcome | Grep + reading | SQL plugin + file tools |", "|---|---:|---:|"]
    for label, key, percentage in (("Accepted as written", "reviewer_acceptance_rate", True), ("Critical-error rate", "critical_error_rate", True),
        ("Essential-fact coverage", "mean_essential_fact_coverage", True), ("Evidence support", "mean_evidence_support", True),
        ("Uncertainty handling", "mean_uncertainty_handling", True), ("Material unsupported claim rate", "material_unsupported_claim_rate", True)):
        display.append(f"| {label} | " + " | ".join(f"{summary[a][key]:.1%}" if percentage else str(summary[a][key]) for a in summary) + " |")
    display += ["", "| Decision | Grep + reading | SQL plugin + file tools |", "|---|---:|---:|"]
    for decision in DECISIONS:
        display.append("| " + decision + " | " + " | ".join(str(summary[a]["decision_counts"][decision]) for a in summary) + " |")
    display += ["", "| Effort / developer feedback | Grep + reading | SQL plugin + file tools |", "|---|---:|---:|"]
    for label, key in (("Mean reviewer correction minutes", "correction_minutes"), ("Mean reviewer follow-up questions", "follow_up_questions"),
        ("Mean developer correction minutes", "user_correction_minutes"), ("Mean developer follow-up questions", "user_follow_up_questions")):
        values = []
        for arm in summary:
            measurement = summary[arm][key]
            values.append(f"{measurement['mean']:.2f} ({measurement['observed']}/{summary[arm]['assigned']} observed)" if measurement["mean"] is not None else "unknown")
        display.append("| " + label + " | " + " | ".join(values) + " |")
    for label, key in (("Developer feedback observed", "observed"), ("Developer accepted as written", "accepted"), ("Developer acceptance rate, all assigned", "all_assigned_rate")):
        values = [summary[a]["actual_user_acceptance"][key] for a in summary]
        display.append("| " + label + " | " + " | ".join("unknown" if v is None else f"{v:.1%}" if key == "all_assigned_rate" else str(v) for v in values) + " |")
    display += ["", "Null means unmeasured, not zero. Developer all-assigned acceptance stays unknown until every assigned answer has explicit developer feedback.", "",
        "| Question | Grep accepted / assigned | Plugin accepted / assigned |", "|---|---:|---:|"]
    for task, arms in per_task.items():
        display.append("| " + task + " | " + " | ".join(f"{v['reviewer_accept_as_written']} / {v['assigned']}" for v in arms.values()) + " |")
    display += ["", "Paired question/repeat outcomes: " + ", ".join(f"{key}={value}" for key, value in pairs.items()) + ".", "",
        "| Secondary resource measure | Grep + reading | SQL plugin + file tools |", "|---|---:|---:|"]
    for label, key, divisor in (("Mean answer time, seconds", "elapsed_ms", 1000),
        ("Mean reported total input tokens", "total_input_tokens", 1), ("Mean reported output tokens", "output_tokens", 1)):
        values = []
        for arm in summary:
            measurement = summary[arm][key]
            values.append(f"{measurement['mean'] / divisor:.2f} ({measurement['observed']}/{summary[arm]['assigned']} observed)" if measurement["mean"] is not None else "unknown")
        display.append("| " + label + " | " + " | ".join(values) + " |")
    display += ["", "Every assigned failure remains in the denominator. Missing usage or feedback is unknown, not zero.", "", result["limitations"]]
    (destination / "acceptance-report.md").write_text("\n".join(display) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    p = sub.add_parser("freeze")
    p.add_argument("--study", type=Path, required=True)
    p.add_argument("--rubric", type=Path, default=DEFAULT_RUBRIC)
    p.add_argument("--output", type=Path, required=True)
    p = sub.add_parser("packet")
    p.add_argument("--review", type=Path, required=True)
    p = sub.add_parser("report")
    p.add_argument("--review", type=Path, required=True)
    p.add_argument("--reviews", type=Path, required=True)
    p.add_argument("--user-feedback", type=Path)
    p.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.action == "freeze":
            freeze(args.study, args.rubric, args.output)
        elif args.action == "packet":
            packet(args.review)
        else:
            report(args.review, args.reviews, args.output, args.user_feedback)
    except (ValueError, KeyError, OSError, TypeError) as exc:
        parser.exit(1, f"Acceptance review failed: {exc}\n")
    print("Acceptance " + args.action + " completed; no model calls or inferred correctness grades.")


if __name__ == "__main__":
    main()
