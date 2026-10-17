import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("rerun", Path(__file__).with_name("rerun-sample-study.py"))
rerun = importlib.util.module_from_spec(spec)
spec.loader.exec_module(rerun)


class RerunTests(unittest.TestCase):
    def artifact(self, root):
        (root / ".claude-plugin").mkdir()
        (root / "mcp").mkdir()
        (root / "server").mkdir()
        (root / "skills").mkdir()
        (root / ".claude-plugin/plugin.json").write_text(json.dumps({"name": "sql-test", "version": "1"}))
        (root / ".mcp.json").write_text(json.dumps({"mcpServers": {"sqlscriptdom-analysis": {
            "command": "${CLAUDE_PLUGIN_ROOT}/mcp/test", "args": ["--stdio"]}}}))
        (root / ".lsp.json").write_text("{}")
        (root / "build.json").write_text(json.dumps({"runtime": rerun.host_runtime(), "version": "1"}))
        (root / "mcp/test").write_bytes(b"not executed")
        return root

    def test_architecture_has_a_python_platform_fallback_when_machine_is_empty(self):
        with patch.object(rerun.platform, "machine", return_value=""), patch.object(rerun.platform, "system", return_value="Windows"), patch.object(rerun.sysconfig, "get_platform", return_value="win-amd64"), patch.dict(rerun.os.environ, {}, clear=True):
            self.assertEqual("win-x64", rerun.host_runtime())

    def test_candidate_fingerprint_detects_changed_bits_without_version_change(self):
        with tempfile.TemporaryDirectory() as folder:
            root = self.artifact(Path(folder))
            before = rerun.candidate(root)
            (root / "mcp/test").write_bytes(b"different native implementation")
            after = rerun.candidate(root)
            self.assertEqual(before["version"], after["version"])
            self.assertNotEqual(before["fingerprint"], after["fingerprint"])

    def test_offline_preflight_rejects_embedded_source_configuration(self):
        with tempfile.TemporaryDirectory() as folder:
            root = self.artifact(Path(folder))
            config = rerun.read(root / ".mcp.json")
            config["mcpServers"]["sqlscriptdom-analysis"]["args"] += ["--config", "online.json"]
            (root / ".mcp.json").write_text(json.dumps(config))
            with self.assertRaisesRegex(ValueError, "Frozen offline"):
                rerun.candidate(root)

    def test_offline_preflight_also_excludes_database_enabled_lsp(self):
        with tempfile.TemporaryDirectory() as folder:
            root = self.artifact(Path(folder))
            (root / ".lsp.json").write_text(json.dumps({"sqlscriptdom": {"args": ["--stdio"], "initializationOptions": {"sql": {"mode": "online"}}}}))
            with self.assertRaisesRegex(ValueError, "Frozen offline"):
                rerun.candidate(root)

    def test_dry_run_does_not_start_model_or_rewrite_prepared_outputs(self):
        with tempfile.TemporaryDirectory() as folder:
            study = Path(folder)
            (study / "rerun-plan.json").write_text("{}")
            with patch.object(rerun, "verify_inputs") as verify, patch.object(rerun, "run_command", return_value=["claude", "fixed-question"]), patch.object(rerun.subprocess, "run") as launch:
                (study / "rerun-plan.json").write_text(json.dumps({"runs": 48}))
                rerun.run(SimpleNamespace(study=study, dry_run=True))
                verify.assert_called_once()
                launch.assert_not_called()
                self.assertFalse((study / "runs").exists())

    def test_blank_grades_cannot_replace_review_and_existing_template_is_preserved(self):
        with tempfile.TemporaryDirectory() as folder:
            study = Path(folder)
            (study / "runs").mkdir()
            (study / "rubric.json").write_text(json.dumps({"tasks": [{"id": "one", "criteria": ["a", "b"]}]}))
            (study / "runs/experiment.json").write_text(json.dumps({"schedule": [{"index": 1, "task_id": "one"}]}))
            rerun.grade_template(SimpleNamespace(study=study))
            grade = rerun.read(study / "grades.template.json")["run-0001"]
            self.assertEqual([None, None], grade["criteria"])
            with self.assertRaises(ValueError):
                rerun.module("report-sample-study").assess({"task_id": "one", "outcome": "success", "exit_code": 0}, grade, {"criteria": ["a", "b"]})
            with self.assertRaises(FileExistsError):
                rerun.grade_template(SimpleNamespace(study=study))

    def test_historical_settings_mismatch_is_visible_and_zero_is_not_a_percent_denominator(self):
        baseline = {"source_revision": "rev", "corpus_sha256": "hash", "rubric_sha256": "gold", "experiment": {"model": "old"}, "summary": {}}
        summary = {"mean_elapsed_ms": 10, "reported_total_input_tokens_mean": 20, "input_tokens_mean": 5,
                   "output_tokens_mean": 2, "mean_factual_score": 1, "mean_citation_score": 1, "strict_passes": 1,
                   "tool_failures": 0, "oversized_tool_results": 0}
        baseline["summary"] = {arm: summary for arm in ("grep", "plugin")}
        current = {"experiment": {"model": "new"}, "summary": baseline["summary"], "comparisons": {}}
        result = rerun.compare(baseline, current, {"source_revision": "rev", "corpus_sha256": "changed", "rubric_sha256": "gold"},
                               {"candidate": {"name": "x", "version": "2", "fingerprint": "bits"}})
        self.assertEqual(["corpus_sha256", "model"], result["mismatches"])
        zero_rows = [row for row in result["metrics"] if row["metric"] == "tool_failures"]
        self.assertTrue(all(row["change_percent"] is None for row in zero_rows))

    def test_snapshot_content_and_inventory_are_verified_before_start(self):
        with tempfile.TemporaryDirectory() as folder:
            study = Path(folder)
            (study / "sql-snapshot").mkdir()
            for name in ("tasks", "rubric"):
                (study / (name + ".json")).write_text("{}")
            sql = study / "sql-snapshot/a.sql"
            sql.write_text("SELECT 1")
            manifest = {name + "_sha256": rerun.digest(study / (name + ".json")) for name in ("tasks", "rubric")}
            manifest["files"] = [{"path": "a.sql", "sha256": rerun.digest(sql)}]
            (study / "manifest.json").write_text(json.dumps(manifest))
            plan = {"runner_sha256": rerun.digest(rerun.REPO / "scripts/evaluate-understanding.py"),
                    "controller_sha256": rerun.digest(Path(rerun.__file__)), "candidate": {"path": "unused", "fingerprint": "same"}}
            with patch.object(rerun, "candidate", return_value={"fingerprint": "same"}):
                rerun.verify_inputs(study, plan)
                sql.write_text("SELECT 2")
                with self.assertRaisesRegex(ValueError, "content changed"):
                    rerun.verify_inputs(study, plan)
                sql.write_text("SELECT 1")
                (study / "sql-snapshot/gold.sql").write_text("UNEXPECTED")
                with self.assertRaisesRegex(ValueError, "inventory changed"):
                    rerun.verify_inputs(study, plan)

    def test_custom_cohort_is_frozen_with_identity_and_never_changes_the_default_suite(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            suite = root / "holdout.json"
            suite.write_text('{"tasks": []}')
            output = root / "study"
            output.mkdir()
            (output / "tasks.json").write_text('[{"id": "holdout", "question": "fresh question"}]')
            args = SimpleNamespace(source=root / "source", plugin=root / "plugin", output=output, suite=suite,
                                   claude=None, model="pinned", repeats=2, workers=2, timeout=180, budget=.5)
            preparation = SimpleNamespace(prepare=lambda source, frozen, destination: self.assertEqual(suite, frozen))
            default_hash = rerun.digest(rerun.SUITE)
            with patch.object(rerun, "candidate", return_value={"version": "candidate", "fingerprint": "bits"}), patch.object(rerun, "client", return_value={"provider_host": "test"}), patch.object(rerun, "module", return_value=preparation):
                rerun.prepare(args)
            plan = rerun.read(output / "rerun-plan.json")
            self.assertEqual(4, plan["runs"])
            self.assertEqual(rerun.digest(suite), plan["suite"]["sha256"])
            self.assertEqual(default_hash, rerun.digest(rerun.SUITE))


if __name__ == "__main__":
    unittest.main()
