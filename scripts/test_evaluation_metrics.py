import importlib.util
import json
import unittest
from pathlib import Path
import tempfile
from types import SimpleNamespace
from unittest.mock import patch, MagicMock

spec = importlib.util.spec_from_file_location("evaluation", Path(__file__).with_name("evaluate-understanding.py"))
evaluation = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evaluation)


class MetricsTests(unittest.TestCase):
    def test_complete_payload_and_brief_delivery_are_counted_without_claiming_accuracy(self):
        brief = {"format": "procedure-brief-v1", "facts": [{"citation": "private 中文.sql:3", "detail": "SECRET"}, {"detail": "uncited"}]}
        payload = {"report": {"format": "paged-report-v1", "status": "Resolved"}, "brief": brief,
                   "coverage": {"limitation": "SECRET"}, "timings": {"coldStart": True}}
        text = json.dumps(payload, ensure_ascii=True, indent=2)
        events = [{"message": {"content": [{"type": "tool_use", "id": "a", "name": "mcp__sql__analyze_procedure"}]}},
                  {"message": {"content": [{"type": "tool_result", "tool_use_id": "a", "content": text}]}}]
        metrics = evaluation.summarize(events + events)
        canonical = lambda value: len(json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8"))
        self.assertEqual(1, metrics["sql_payloads"])
        self.assertEqual(canonical(payload), metrics["sql_payload_utf8_bytes"])
        self.assertEqual(len(text.encode("utf-8")), metrics["sql_result_text_utf8_bytes"])
        self.assertEqual(canonical(payload["report"]), metrics["analysis_report_utf8_bytes"])
        self.assertEqual(canonical(brief), metrics["analysis_brief_utf8_bytes"])
        self.assertEqual(1, metrics["analysis_briefs"])
        self.assertEqual(1, metrics["analysis_cited_brief_facts"])
        self.assertNotIn("SECRET", str(metrics))
        self.assertNotIn("private", str(metrics))
        self.assertIsNone(metrics["input_tokens"])

    def test_null_missing_and_malformed_briefs_are_not_evidence_delivery(self):
        for brief in (None, {}, {"format": "unknown", "facts": [{"citation": "p.sql:1"}]},
                      {"format": "procedure-brief-v1", "facts": None}):
            events = [{"message": {"content": [{"type": "tool_use", "id": "a", "name": "mcp__sql__analyze_procedure"}]}},
                      {"message": {"content": [{"type": "tool_result", "tool_use_id": "a", "content": json.dumps({"report": {}, "brief": brief})}]}}]
            metrics = evaluation.summarize(events)
            self.assertEqual(0, metrics["analysis_cited_brief_facts"])
            self.assertEqual(1, metrics["sql_payloads"])
        self.assertIsNone(evaluation.summarize([])["sql_payload_utf8_bytes"])

    def test_client_oversize_result_is_a_failure_even_without_is_error_flag(self):
        call = {"type": "assistant", "message": {"content": [{"type": "tool_use", "id": "a", "name": "mcp__sql__find_procedures"}]}}
        result = {"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "a", "content": "Error: result (217707 characters) exceeds maximum allowed tokens. Output saved."}]}}
        metrics = evaluation.summarize([call, result])
        self.assertEqual(1, metrics["tool_failures"])
        self.assertEqual(1, metrics["oversized_tool_results"])
        self.assertEqual(0, metrics["server_timed_calls"])

    def test_client_configuration_is_isolated_and_only_requested_plugin_is_wired(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            workspace, plugin, output = root / "sql", root / "plugin", root / "results"
            workspace.mkdir()
            (workspace / "a.sql").write_text("SELECT 1")
            (workspace / "CLAUDE.md").write_text("HIDDEN GOLD")
            (plugin / ".claude-plugin").mkdir(parents=True)
            (plugin / ".claude-plugin/plugin.json").write_text(json.dumps({"name": "test-sql", "version": "1"}))
            (plugin / ".mcp.json").write_text(json.dumps({"mcpServers": {"sqlscriptdom-analysis": {
                "command": "${CLAUDE_PLUGIN_ROOT}/mcp/server.exe", "args": ["--stdio"]}}}))
            args = SimpleNamespace(workspace=workspace, plugin=plugin, output=output, restricted=True,
                                   claude="claude", model="fixed-model", effort="low", budget=.5, timeout=10,
                                   keep_transcripts=True, keep_answers=True, revision="rev", cli_version="cli", plugin_version="1")
            def fake_process(command, **options):
                copied = Path(options["cwd"])
                self.assertTrue((copied / "a.sql").is_file())
                self.assertFalse((copied / "CLAUDE.md").exists())
                self.assertIn("--restricted", command)
                self.assertIn("--strict-mcp-config", command)
                self.assertEqual("", command[command.index("--setting-sources") + 1])
                self.assertNotIn("SQLLSP_MCP_CONFIG", options["env"])
                process = MagicMock()
                process.returncode = 0
                process.communicate.return_value = (json.dumps({"type": "result", "subtype": "success", "result": "answer"}), "")
                process.__enter__.return_value = process
                return process
            with patch.object(evaluation.subprocess, "Popen", side_effect=fake_process) as launch:
                for index, arm in enumerate(("grep", "plugin"), 1):
                    row = evaluation.run_one(args, {"id": "a", "question": "Question"}, arm, 1, index)
                    self.assertEqual("success", row["outcome"])
                    command = launch.call_args.args[0]
                    servers = json.loads(command[command.index("--mcp-config") + 1])["mcpServers"]
                    if arm == "grep":
                        self.assertEqual({}, servers)
                        self.assertNotIn("--plugin-dir", command)
                        self.assertEqual("Read,Grep,Glob", command[command.index("--tools") + 1])
                    else:
                        self.assertEqual(["plugin:test-sql:sqlscriptdom-analysis"], list(servers))
                        self.assertNotIn("${CLAUDE_PLUGIN_ROOT}", servers[next(iter(servers))]["command"])

    def test_baseline_first_schedule_has_matching_pairs_and_no_phase_overlap(self):
        tasks = [{"id": "a", "question": "a"}, {"id": "b", "question": "b"}]
        schedule = evaluation.make_schedule(tasks, 2, 42, "baseline-first")
        self.assertEqual(["grep"] * 4 + ["plugin"] * 4, [arm for _, arm, _ in schedule])
        self.assertEqual([(task, repeat) for task, _, repeat in schedule[:4]],
                         [(task, repeat) for task, _, repeat in schedule[4:]])
        self.assertEqual(schedule, evaluation.make_schedule(tasks, 2, 42, "baseline-first"))

    def test_random_schedule_preserves_all_pairs_and_single_arm_is_possible(self):
        tasks = [{"id": "a", "question": "a"}, {"id": "b", "question": "b"}]
        schedule = evaluation.make_schedule(tasks, 3, 42)
        self.assertEqual(12, len(schedule))
        self.assertEqual(12, len({(task["id"], arm, repeat) for task, arm, repeat in schedule}))
        self.assertEqual(6, len(evaluation.make_schedule(tasks, 3, 42, "baseline-first", ["grep"])))

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

    def test_analysis_cache_counters_and_io_intervals_do_not_retain_cache_paths(self):
        payload = {"report": {}, "cache": {"enabled": True, "status": "Hit", "file": "/private/SECRET/report.json"},
                   "timings": {"analysisMs": 0, "cacheValidationMs": 12, "cacheReadMs": 3, "cacheWriteMs": 0}}
        events = [{"type": "assistant", "message": {"content": [{"type": "tool_use", "id": "a", "name": "mcp__sql__analyze_procedure"}]}},
                  {"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "a", "content": json.dumps(payload)}]}}]
        metrics = evaluation.summarize(events)
        self.assertEqual(1, metrics["analysis_cache_hits"])
        self.assertEqual(1, metrics["analysis_cache_enabled_calls"])
        self.assertEqual(0, metrics["server_analysis_ms"])
        self.assertEqual(12, metrics["server_cache_validation_ms"])
        self.assertEqual(3, metrics["server_cache_read_ms"])
        self.assertNotIn("SECRET", str(metrics))
        self.assertIsNone(evaluation.summarize([])["analysis_cache_hits"])


if __name__ == "__main__":
    unittest.main()
