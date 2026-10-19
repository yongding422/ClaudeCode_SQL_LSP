import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


spec = importlib.util.spec_from_file_location("acceptance", Path(__file__).with_name("review-answer-acceptance.py"))
acceptance = importlib.util.module_from_spec(spec)
spec.loader.exec_module(acceptance)


def write(path, value):
    path.write_text(json.dumps(value), encoding="utf-8")


class AcceptanceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.study = self.root / "study"
        self.study.mkdir()
        (self.study / "sql-snapshot").mkdir()
        sql = self.study / "sql-snapshot/source.sql"
        sql.write_text("CREATE PROC dbo.P AS SELECT 1;\n", encoding="utf-8")
        self.suite = {"version": 1, "source_revision": "revision", "tasks": [
            {"id": "Q1", "question": "What does P do?", "criteria": ["Returns one"], "evidence": [["source.sql", 1, 1]]}]}
        write(self.study / "rubric.json", self.suite)
        write(self.study / "tasks.json", [{"id": "Q1", "question": "What does P do?"}])
        write(self.study / "manifest.json", {"source_revision": "revision", "corpus_sha256": "corpus",
            "rubric_sha256": acceptance.sha(self.study / "rubric.json"), "tasks_sha256": acceptance.sha(self.study / "tasks.json"),
            "files": [{"path": "source.sql", "sha256": acceptance.sha(sql)}]})
        self.task = {"task_id": "Q1", "developer_goal": "Understand the result", "essential_facts": ["Returns one"],
            "critical_errors": ["Claims a table is updated"], "required_evidence": ["Cite the SELECT"],
            "expected_uncertainty": ["Runtime caller is unknown"], "optional_detail": ["Whitespace"]}
        self.rubric = self.root / "acceptance.json"
        write(self.rubric, {"format": "sql-answer-acceptance-v1", "source_revision": "revision",
            "suite_sha256": hashlib.sha256(json.dumps(self.suite, sort_keys=True).encode()).hexdigest(), "tasks": [self.task]})
        self.review = self.root / "review"
        acceptance.freeze(self.study, self.rubric, self.review)

    def runs(self, missing=False):
        runs = self.study / "runs"
        runs.mkdir()
        schedule = []
        for i, arm in enumerate(("grep", "plugin"), 1):
            schedule.append({"index": i, "task_id": "Q1", "arm": arm, "repeat": 1})
            if missing and arm == "plugin":
                continue
            folder = runs / f"run-{i:04d}"
            folder.mkdir()
            (folder / "answer.txt").write_text("It returns 1. source.sql:1\n", encoding="utf-8")
            write(folder / "metrics.json", {"task_id": "Q1", "arm": arm, "repeat": 1,
                "outcome": "success", "exit_code": 0, "elapsed_ms": 10,
                "input_tokens": 10, "cache_read_tokens": 20, "cache_write_tokens": None, "output_tokens": 3})
        write(runs / "experiment.json", {"repeats": 1, "schedule": schedule})

    def packet(self, missing=False):
        self.runs(missing)
        acceptance.packet(self.review)
        self.reviews = self.review / "reviewer/reviews.json"
        data = acceptance.read(self.review / "reviewer/reviews.template.json")
        data["reviewer"] = {"id": "synthetic test reviewer", "type": "independent_human", "arm_labels_seen": False}
        mapping = acceptance.read(self.review / "private/mapping.json")["answers"]
        for case, grade in data["answers"].items():
            grade.update(essential_facts=[1], evidence_support=1, uncertainty_handling=1, relevance=1,
                critical_errors=[], material_unsupported_claims=[], decision="accept_as_written",
                notes="Synthetic fixture review, not a real developer judgment.")
            if missing and mapping[case]["arm"] == "plugin":
                grade.update(essential_facts=[0], evidence_support=0, uncertainty_handling=0, relevance=0,
                    decision="reject", notes="Missing answer.")
        write(self.reviews, data)
        return data, mapping

    def result(self, user=None):
        output = self.root / "report"
        acceptance.report(self.review, self.reviews, output, user)
        return acceptance.read(output / "acceptance-results.json")

    def test_end_to_end_preserves_history_and_distinguishes_unknown_user_acceptance(self):
        originals = {p: acceptance.sha(p) for p in self.study.rglob("*") if p.is_file()}
        self.packet()
        result = self.result()
        self.assertEqual("prospective", result["contract"]["timing"])
        self.assertEqual(1, result["summary"]["plugin"]["reviewer_acceptance_rate"])
        self.assertEqual({"observed": 0, "unknown": 1, "accepted": 0, "observed_rate": None, "all_assigned_rate": None},
            result["summary"]["plugin"]["actual_user_acceptance"])
        self.assertIsNone(result["summary"]["plugin"]["total_input_tokens"]["mean"])
        self.assertIsNone(result["summary"]["plugin"]["correction_minutes"]["mean"])
        self.assertEqual(1, result["paired_acceptance"]["both_accepted"])
        for path, digest in originals.items():
            self.assertEqual(digest, acceptance.sha(path))

    def test_packet_omits_arm_timing_and_tool_metadata_and_does_not_prefill_grades(self):
        self.runs()
        acceptance.packet(self.review)
        template = acceptance.read(self.review / "reviewer/reviews.template.json")
        for case, grade in template["answers"].items():
            self.assertEqual([None], grade["essential_facts"])
            self.assertIsNone(grade["decision"])
            self.assertIsNone(grade["critical_errors"])
            self.assertNotIn("run-", case)
            self.assertNotIn("arm", grade)
            self.assertNotIn("elapsed_ms", grade)
        self.assertNotIn("metrics.json", (self.review / "reviewer/README.md").read_text())

    def test_missing_runs_remain_denominator_and_are_not_accepted(self):
        self.packet(missing=True)
        result = self.result()
        self.assertEqual(1, result["summary"]["plugin"]["assigned"])
        self.assertEqual(0, result["summary"]["plugin"]["successful_answers"])
        self.assertEqual(0, result["summary"]["plugin"]["reviewer_acceptance_rate"])
        self.assertEqual(0, result["summary"]["plugin"]["mean_essential_fact_coverage"])
        self.assertEqual(1, result["paired_acceptance"]["grep_only_accepted"])

    def test_failed_answer_cannot_be_accepted_even_with_all_facts_checked(self):
        grade = self.packet()[0]["answers"].popitem()[1]
        with self.assertRaisesRegex(ValueError, "Acceptance contradicts"):
            acceptance.validate_review(grade, self.task, False)

    def test_extra_critical_or_unsupported_claim_blocks_acceptance_despite_full_coverage(self):
        grade = self.packet()[0]["answers"].popitem()[1]
        for kind in ("critical_errors", "material_unsupported_claims"):
            invalid = copy.deepcopy(grade)
            invalid[kind] = [{"claim": "Updates a table", "explanation": "Unsupported", "evidence": "source.sql:1 SELECT only"}]
            with self.assertRaisesRegex(ValueError, "Acceptance contradicts"):
                acceptance.validate_review(invalid, self.task, True)
            invalid["decision"] = "major_correction"
            acceptance.validate_review(invalid, self.task, True)

    def test_missing_essential_fact_or_uncertainty_cannot_be_dismissed_as_minor(self):
        grade = self.packet()[0]["answers"].popitem()[1]
        for key, value in (("essential_facts", [.5]), ("uncertainty_handling", .5)):
            invalid = copy.deepcopy(grade)
            invalid.update(decision="minor_edits", **{key: value})
            with self.assertRaises(ValueError):
                acceptance.validate_review(invalid, self.task, True)
        grade.update(decision="minor_edits", evidence_support=.5, relevance=.5)
        acceptance.validate_review(grade, self.task, True)

    def test_null_boolean_or_out_of_range_scores_and_nonfinite_effort_are_rejected(self):
        grade = self.packet()[0]["answers"].popitem()[1]
        for value in (None, True, 2, float("nan")):
            invalid = dict(grade, essential_facts=[value])
            with self.assertRaises(ValueError):
                acceptance.validate_review(invalid, self.task, True)
        for value in (-1, True, float("nan"), float("inf")):
            with self.assertRaises(ValueError):
                acceptance.validate_review(dict(grade, correction_minutes=value), self.task, True)
        with self.assertRaises(ValueError):
            acceptance.validate_review(dict(grade, follow_up_questions=.5), self.task, True)

    def test_partial_human_feedback_is_not_an_all_assigned_user_acceptance_result(self):
        self.packet()
        path = self.root / "user-feedback.json"
        data = acceptance.read(self.review / "reviewer/user-feedback.template.json")
        data["respondent"]["id"] = "Synthetic human fixture, not collected feedback"
        next(iter(data["answers"].values())).update(decision="accept_as_written", notes="Fixture", correction_minutes=0, follow_up_questions=0)
        write(path, data)
        result = self.result(path)
        self.assertEqual(1, sum(a["actual_user_acceptance"]["observed"] for a in result["summary"].values()))
        unknown = next(a for a in result["summary"].values() if a["actual_user_acceptance"]["observed"] == 0)
        self.assertIsNone(unknown["actual_user_acceptance"]["all_assigned_rate"])
        self.assertIsNone(unknown["user_correction_minutes"]["mean"])

    def test_agent_feedback_cannot_be_labeled_actual_user_feedback(self):
        self.packet()
        path = self.root / "user-feedback.json"
        data = acceptance.read(self.review / "reviewer/user-feedback.template.json")
        data["respondent"] = {"id": "agent", "type": "implementing_agent"}
        write(path, data)
        with self.assertRaisesRegex(ValueError, "human developer"):
            self.result(path)

    def test_partial_feedback_within_one_arm_does_not_imply_complete_user_acceptance(self):
        self.packet()
        result = self.result()
        first = next(row for row in result["runs"] if row["arm"] == "plugin")
        first.update(user_decision="accept_as_written", user_correction_minutes=0, user_follow_up_questions=0)
        second = dict(first, repeat=2, user_decision=None, user_correction_minutes=None, user_follow_up_questions=None)
        summary = acceptance.aggregate([first, second])
        self.assertEqual(1, summary["actual_user_acceptance"]["observed_rate"])
        self.assertIsNone(summary["actual_user_acceptance"]["all_assigned_rate"])
        self.assertEqual(1, summary["actual_user_acceptance"]["unknown"])
        self.assertEqual({"observed": 1, "unknown": 1, "mean": 0}, summary["user_correction_minutes"])

    def test_report_cannot_accept_failed_answer_based_on_human_feedback(self):
        _, mapping = self.packet(missing=True)
        path = self.root / "user-feedback.json"
        data = acceptance.read(self.review / "reviewer/user-feedback.template.json")
        data["respondent"]["id"] = "Synthetic human fixture"
        missing = next(case for case, entry in mapping.items() if entry["arm"] == "plugin")
        data["answers"][missing].update(decision="accept_as_written", notes="Contradicts missing answer")
        write(path, data)
        with self.assertRaisesRegex(ValueError, "Invalid actual user feedback"):
            self.result(path)

    def test_review_cannot_drop_a_hard_answer(self):
        data, _ = self.packet()
        data["answers"].popitem()
        write(self.reviews, data)
        with self.assertRaisesRegex(ValueError, "every assigned"):
            self.result()

    def test_answer_metrics_rubric_and_source_tampering_are_detected(self):
        _, mapping = self.packet()
        case = next(iter(mapping))
        paths = [self.study / "runs" / mapping[case]["run"] / "answer.txt",
            self.study / "runs" / mapping[case]["run"] / "metrics.json",
            self.review / "reviewer/answers" / f"{case}.txt", self.review / "reviewer/rubric.json",
            self.study / "sql-snapshot/source.sql", self.review / "rubric.json"]
        for path in paths:
            with self.subTest(path=path):
                before = path.read_bytes()
                path.write_bytes(before + b" ")
                try:
                    with self.assertRaises(ValueError):
                        self.result()
                finally:
                    path.write_bytes(before)

    def test_retroactive_rubric_is_explicit_and_output_does_not_overwrite(self):
        self.runs()
        other = self.root / "retro"
        acceptance.freeze(self.study, self.rubric, other)
        self.assertEqual("retrospective", acceptance.read(other / "contract.json")["timing"])
        with self.assertRaises(FileExistsError):
            acceptance.freeze(self.study, self.rubric, other)
        acceptance.packet(self.review)
        with self.assertRaises(FileExistsError):
            acceptance.packet(self.review)

    def test_duplicate_or_incomplete_pair_schedule_is_rejected(self):
        self.runs()
        path = self.study / "runs/experiment.json"
        data = acceptance.read(path)
        for schedule in ([data["schedule"][0]], [data["schedule"][0]] * 2):
            write(path, dict(data, schedule=schedule))
            with self.assertRaises(ValueError):
                acceptance.scheduled_inputs(self.study)

    def test_wrong_suite_or_missing_acceptance_tasks_are_rejected(self):
        rubric = acceptance.read(self.rubric)
        for invalid in (dict(rubric, suite_sha256="different"), dict(rubric, tasks=[])):
            with self.assertRaises(ValueError):
                acceptance.validate_rubric(invalid, self.suite, rubric["suite_sha256"])

    def test_shipped_rubrics_match_their_suites_and_keep_historical_cohort_separate(self):
        directory = Path(__file__).resolve().parents[1] / "docs/evaluation"
        for suite_name, rubric_name, count in (("sql-server-samples-suite.json", "sql-server-samples-acceptance-v1.json", 12),
            ("sql-server-samples-investigations-v1.json", "sql-server-samples-investigations-acceptance-v1.json", 5),
            ("sql-server-samples-new-procedures-v1.json", "sql-server-samples-new-procedures-acceptance-v1.json", 3)):
            suite, rubric = acceptance.read(directory / suite_name), acceptance.read(directory / rubric_name)
            acceptance.validate_rubric(rubric, suite, hashlib.sha256(json.dumps(suite, sort_keys=True).encode()).hexdigest())
            self.assertEqual(count, len(rubric["tasks"]))

    def test_new_primary_procedures_do_not_reuse_previous_cohort_evidence_targets(self):
        directory = Path(__file__).resolve().parents[1] / "docs/evaluation"
        old_sources = {evidence[0] for name in ("sql-server-samples-suite.json", "sql-server-samples-additional-suite.json",
                      "sql-server-samples-investigations-v1.json") for task in acceptance.read(directory / name)["tasks"] for evidence in task["evidence"]}
        suite = acceptance.read(directory / "sql-server-samples-new-procedures-v1.json")
        primary = [task["primary_source"] for task in suite["tasks"]]
        self.assertEqual(3, len(set(primary)))
        self.assertFalse(set(primary) & old_sources)
        self.assertTrue(all(task["primary_source"] in {entry[0] for entry in task["evidence"]} for task in suite["tasks"]))


if __name__ == "__main__":
    unittest.main()
