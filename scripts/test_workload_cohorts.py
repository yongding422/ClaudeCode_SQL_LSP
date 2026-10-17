import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("cohorts", Path(__file__).with_name("summarize-workload-cohorts.py"))
cohorts = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cohorts)


class CohortTests(unittest.TestCase):
    def result(self):
        return {"experiment": {"repeats": 1, "workflow": "available"}, "runs": [
            {"task_id": f"H{i:02d}-fixture", "repeat": 1, "arm": arm, "elapsed_ms": 100 if arm == "grep" else 75,
             "reported_total_input_tokens": 100, "factual_score": 1, "citation_score": 1, "strict_pass": True,
             "material_unsupported_claim": False, "tool_failures": 0, "oversized_tool_results": 0}
            for i in range(1, 7) for arm in ("grep", "plugin")]}

    def test_distinct_targets_and_quality_regression_are_visible(self):
        value = self.result()
        groups = cohorts.summarize(value)["groups"]
        self.assertTrue(groups["simple"]["combined_target_met"])
        self.assertTrue(groups["structural"]["combined_target_met"])
        value["runs"][-1]["material_unsupported_claim"] = True
        self.assertFalse(cohorts.summarize(value)["groups"]["structural"]["combined_target_met"])

    def test_missing_usage_and_wrong_workflow_do_not_become_success(self):
        value = self.result()
        value["runs"][1]["reported_total_input_tokens"] = None
        self.assertFalse(cohorts.summarize(value)["groups"]["simple"]["combined_target_met"])
        value["experiment"]["workflow"] = "prescribed"
        self.assertIsNone(cohorts.summarize(value)["groups"]["simple"]["combined_target_met"])

    def test_missing_failed_or_duplicate_sessions_cannot_be_dropped(self):
        value = self.result()
        value["runs"].pop()
        with self.assertRaisesRegex(ValueError, "unmatched"):
            cohorts.summarize(value)
        value = self.result()
        value["runs"].append(value["runs"][-1])
        with self.assertRaisesRegex(ValueError, "duplicate"):
            cohorts.summarize(value)
