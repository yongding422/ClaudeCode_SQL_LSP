import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("report", Path(__file__).with_name("report-sample-study.py"))
report = importlib.util.module_from_spec(spec)
spec.loader.exec_module(report)


class StudyReviewTests(unittest.TestCase):
    def test_string_messages_are_not_mistaken_for_tool_content_and_unresolved_is_visible(self):
        self.assertEqual([], report.blocks({"message": "client status"}))
        self.assertEqual([], report.blocks({"message": {"content": "plain text"}}))
        events = [{"message": "status"}, {"message": {"content": [{"type": "tool_result", "content": '{"report":{"status":"Unresolved"}}'}]}}]
        self.assertEqual(["Unresolved"], report.analysis_statuses(events))

    def test_cached_input_is_counted_separately_and_missing_usage_is_not_zero(self):
        self.assertEqual(125, report.input_total({"input_tokens": 20, "cache_read_tokens": 100, "cache_write_tokens": 5}))
        self.assertIsNone(report.input_total({"input_tokens": 20, "cache_read_tokens": 100}))

    def test_failed_session_cannot_be_graded_as_a_passing_answer(self):
        grade = {"criteria": [1, 1], "citations": 1, "material_unsupported_claim": False, "notes": "Reviewed."}
        row = {"task_id": "x", "outcome": "timeout", "exit_code": -1}
        reviewed = report.assess(row, grade, {"criteria": ["a", "b"]})
        self.assertEqual(0, reviewed["factual_score"])
        self.assertEqual(0, reviewed["citation_score"])
        self.assertFalse(reviewed["strict_pass"])

    def test_material_unsupported_claim_blocks_pass_even_with_all_required_facts(self):
        grade = {"criteria": [1, 1], "citations": 1, "material_unsupported_claim": True, "notes": "Adds false side-effect guarantee."}
        row = {"task_id": "x", "outcome": "success", "exit_code": 0}
        reviewed = report.assess(row, grade, {"criteria": ["a", "b"]})
        self.assertEqual(1, reviewed["factual_score"])
        self.assertFalse(reviewed["strict_pass"])

    def test_unreviewed_or_mismatched_rubric_entries_are_rejected(self):
        row = {"task_id": "x", "outcome": "success", "exit_code": 0}
        for scores in ([1], [True, 1], [2, 1]):
            with self.assertRaises(ValueError):
                report.assess(row, {"criteria": scores, "citations": 1, "material_unsupported_claim": False, "notes": "Reviewed."}, {"criteria": ["a", "b"]})


if __name__ == "__main__":
    unittest.main()
