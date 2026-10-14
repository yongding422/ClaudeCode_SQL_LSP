import importlib.util
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


if __name__ == "__main__":
    unittest.main()
