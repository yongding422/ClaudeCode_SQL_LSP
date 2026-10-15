import importlib.util
import json
import unittest
from pathlib import Path
import tempfile

spec = importlib.util.spec_from_file_location("evaluation", Path(__file__).with_name("evaluate-understanding.py"))
evaluation = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evaluation)


class MetricsTests(unittest.TestCase):
    def test_correlates_ids_deduplicates_and_retains_client_failures(self):
        call = {"type": "assistant", "message": {"content": [{"type": "tool_use", "id": "a", "name": "LSP"}]}}
        result = {"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "a", "content": "Error performing outgoingCalls: starting"}]}}
        metrics = evaluation.summarize([call, call, result])
        self.assertEqual(1, metrics["lsp_calls"])
        self.assertEqual(1, metrics["tool_failures"])
        self.assertEqual(0, metrics["missing_tool_results"])
        self.assertEqual("missing-result", metrics["outcome"])

    def test_missing_usage_is_unknown_and_missing_result_counts(self):
        metrics = evaluation.summarize([{"type": "assistant", "message": {"content": [{"type": "tool_use", "id": "a", "name": "mcp__plugin_sqlscriptdom__analyze_procedure"}]}}])
        self.assertEqual(1, metrics["mcp_calls"])
        self.assertEqual(1, metrics["missing_tool_results"])
        self.assertIsNone(metrics["input_tokens"])
        self.assertIsNone(metrics["reported_cost_usd"])

    def test_aggregate_keeps_usage_without_sql_or_arguments(self):
        metrics = evaluation.summarize([{"type": "assistant", "message": {"content": [{"type": "tool_use", "id": "a", "name": "Read", "input": {"path": "private.sql", "sql": "SECRET"}}]}},
                                       {"type": "result", "subtype": "success", "total_cost_usd": 0.02, "usage": {"input_tokens": 40, "output_tokens": 50, "cache_read_input_tokens": 100}}])
        self.assertEqual(40, metrics["input_tokens"])
        self.assertEqual(100, metrics["cache_read_tokens"])
        self.assertNotIn("SECRET", str(metrics))
        self.assertNotIn("private.sql", str(metrics))

    def test_workspace_copy_hides_answers_configuration_and_non_sql(self):
        with tempfile.TemporaryDirectory() as source, tempfile.TemporaryDirectory() as target:
            root = Path(source)
            (root / "a.sql").write_text("SELECT 1;")
            (root / "gold.json").write_text("SECRET")
            (root / "CLAUDE.md").write_text("SECRET")
            (root / "bin").mkdir()
            (root / "bin/b.sql").write_text("SECRET")
            self.assertEqual(1, evaluation.copy_sql(root, Path(target)))
            self.assertEqual(["a.sql"], [p.name for p in Path(target).iterdir()])

    def test_linked_sql_sources_outside_workspace_are_not_copied(self):
        with tempfile.TemporaryDirectory() as source, tempfile.TemporaryDirectory() as target, tempfile.TemporaryDirectory() as outside:
            private = Path(outside) / "private.sql"
            private.write_text("SECRET")
            try:
                (Path(source) / "link.sql").symlink_to(private)
            except OSError:
                self.skipTest("Host does not allow creating symbolic links.")
            self.assertEqual(0, evaluation.copy_sql(Path(source), Path(target)))

    def test_server_timings_are_correlated_deduplicated_and_separate_from_task_time(self):
        def pair(identity, method, payload, blocks=False):
            text = json.dumps(payload)
            return [{"type": "assistant", "message": {"content": [{"type": "tool_use", "id": identity, "name": "mcp__plugin_sql__" + method}]}},
                    {"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": identity,
                       "content": [{"type": "text", "text": text}] if blocks else text}]}}]
        events = pair("a", "find_procedures", {"timings": {"coldStart": True, "initializationMs": 12, "navigationMs": 8, "analysisMs": 2}})
        events += pair("b", "analyze_procedure", {"timings": {"coldStart": False, "analysisMs": 3, "presentationMs": .25},
                       "report": {"private": "SECRET 中文"}}, True)
        metrics = evaluation.summarize(events + events)
        self.assertEqual(2, metrics["server_timed_calls"])
        self.assertEqual(1, metrics["server_cold_start_calls"])
        self.assertEqual(12, metrics["server_initialization_ms"])
        self.assertEqual(5, metrics["server_analysis_ms"])
        self.assertEqual(.25, metrics["server_presentation_ms"])
        self.assertIsNone(metrics["server_queue_ms"])
        self.assertEqual(len('{"private":"SECRET 中文"}'.encode("utf-8")), metrics["analysis_report_utf8_bytes"])
        self.assertNotIn("SECRET", str(metrics))

    def test_missing_old_invalid_failed_and_non_mcp_timings_stay_unknown(self):
        events = []
        for identity, name, content, error in [
                ("old", "mcp__sql__analyze_procedure", '{"report":{}}', False),
                ("read", "Read", '{"timings":{"analysisMs":999}}', False),
                ("failed", "mcp__sql__find_procedures", '{"timings":{"analysisMs":999}}', True),
                ("invalid", "mcp__sql__find_procedures", '{"timings":{"analysisMs":-1,"queueMs":true,"navigationMs":"private","initializationMs":NaN}}', False)]:
            events += [{"type": "assistant", "message": {"content": [{"type": "tool_use", "id": identity, "name": name}]}},
                       {"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": identity, "content": content, "is_error": error}]}}]
        metrics = evaluation.summarize(events)
        self.assertIsNone(metrics["server_analysis_ms"])
        self.assertIsNone(metrics["server_initialization_ms"])
        self.assertIsNone(metrics["server_cold_start_calls"])
        self.assertEqual(1, metrics["analysis_reports"])
        self.assertIsNone(evaluation.summarize([])["analysis_report_utf8_bytes"])


if __name__ == "__main__":
    unittest.main()
