"""Classify retained client tool failures without replaying a model or exposing SQL."""
import argparse
import collections
import json
from pathlib import Path


def classify(runs):
    failures = []
    for transcript in sorted(Path(runs).glob("run-*/session.jsonl")):
        requests = {}
        seen = set()
        for line in transcript.read_text(encoding="utf-8").splitlines():
            event = json.loads(line)
            message = event.get("message")
            if not isinstance(message, dict):
                continue
            content = message.get("content", [])
            if not isinstance(content, list):
                continue
            for block in content:
                if block.get("type") == "tool_use":
                    requests[block["id"]] = block.get("name", "unknown")
                if block.get("type") != "tool_result" or block.get("tool_use_id") in seen:
                    continue
                seen.add(block.get("tool_use_id"))
                value = block.get("content", "")
                text = value if isinstance(value, str) else json.dumps(value)
                if "exceeds maximum allowed tokens" in text:
                    category = "oversized-result"
                elif block.get("is_error") or "Error performing " in text:
                    if "--restricted confines" in text:
                        category = "restricted-path-mismatch"
                    elif "File does not exist" in text or "does not exist" in text or "ENOENT" in text:
                        category = "missing-path"
                    elif "Path does not exist" in text:
                        category = "missing-path"
                    else:
                        category = "other-client-error"
                else:
                    continue
                failures.append({"run": transcript.parent.name, "tool": requests.get(block.get("tool_use_id"), "unknown"),
                                 "category": category, "message": text[:500] if category != "oversized-result" else "Saved oversized result rejected by client"})
    return {"counts": dict(collections.Counter(f["category"] for f in failures)), "failures": failures}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runs", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = classify(args.runs)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result["counts"]))
