"""Apply predeclared cohort targets to reviewed runs; never infer correctness grades."""
import argparse
import hashlib
import json
import math
from pathlib import Path
from statistics import mean, median


def summarize(result):
    rows = result["runs"]
    ids = {row["task_id"].split("-")[0] for row in rows}
    workflow = result["experiment"].get("workflow", "prescribed")
    if ids == {f"Q{i:02d}" for i in range(1, 13)}:
        controls = {"Q03", "Q09", "Q10", "Q11"}
    elif ids == {f"H{i:02d}" for i in range(1, 7)}:
        controls = {"H01", "H02"}
    else:
        raise ValueError("Unrecognized frozen cohort; declare its groups before model runs.")
    output = {"workflow": workflow, "groups": {}, "limitations": "Selected related tasks, implementing-agent grades, few repeats and uncontrolled provider/OS caches; descriptive targets, not statistical superiority."}
    for name, selected in (("simple", controls), ("structural", ids - controls)):
        grouped = {arm: [row for row in rows if row["arm"] == arm and row["task_id"].split("-")[0] in selected] for arm in ("grep", "plugin")}
        keys = [{(row["task_id"], row["repeat"]) for row in grouped[arm]} for arm in ("grep", "plugin")]
        expected = len(selected) * result["experiment"]["repeats"]
        if keys[0] != keys[1] or len(keys[0]) != expected or any(len(values) != expected for values in grouped.values()):
            raise ValueError("Missing, duplicate or unmatched assigned sessions.")
        arms = {}
        for arm, values in grouped.items():
            times = sorted(row["elapsed_ms"] for row in values)
            tokens = [row["reported_total_input_tokens"] for row in values]
            known = all(isinstance(value, (int, float)) for value in tokens)
            arms[arm] = {"runs": len(values), "mean_ms": mean(times), "median_ms": median(times),
                         "p95_nearest_rank_ms": times[math.ceil(.95 * len(times)) - 1],
                         "mean_total_input": mean(tokens) if known else None,
                         "median_total_input": median(tokens) if known else None,
                         "factual": mean(row["factual_score"] for row in values),
                         "citations": mean(row["citation_score"] for row in values),
                         "strict_passes": sum(row["strict_pass"] for row in values),
                         "material_unsupported": sum(row["material_unsupported_claim"] for row in values),
                         "tool_failures": sum(row["tool_failures"] for row in values),
                         "overflows": sum(row["oversized_tool_results"] for row in values)}
        baseline, plugin = arms["grep"], arms["plugin"]
        quality = plugin["factual"] >= baseline["factual"] and plugin["citations"] >= baseline["citations"] and plugin["strict_passes"] >= baseline["strict_passes"] and plugin["material_unsupported"] <= baseline["material_unsupported"]
        def ratio(field):
            a, b = baseline[field], plugin[field]
            return b / a if a and b is not None else None
        time_ratio = ratio("median_ms" if name == "simple" else "mean_ms")
        token_ratio = ratio("median_total_input" if name == "simple" else "mean_total_input")
        applicable = name != "simple" or workflow == "available"
        resources = (time_ratio is not None and token_ratio is not None and time_ratio <= 1.1 and token_ratio <= 1.1) if name == "simple" else any(value is not None and value <= .8 for value in (time_ratio, token_ratio))
        output["groups"][name] = {"task_ids": sorted(selected), "arms": arms, "time_ratio": time_ratio,
                                   "total_input_ratio": token_ratio, "quality_gate_met": quality,
                                   "target_applicable": applicable, "resource_target_met": resources if applicable else None,
                                   "combined_target_met": quality and resources if applicable else None}
    return output


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reviewed-results", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    evidence = args.reviewed_results.read_bytes()
    value = summarize(json.loads(evidence))
    value["reviewed_results_sha256"] = hashlib.sha256(evidence).hexdigest()
    args.output.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
