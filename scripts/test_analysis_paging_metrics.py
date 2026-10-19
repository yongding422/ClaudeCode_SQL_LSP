import importlib.util
import json
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("page_report", Path(__file__).with_name("report-sample-study.py"))
report = importlib.util.module_from_spec(spec)
spec.loader.exec_module(report)


class PagedEvidenceMetrics(unittest.TestCase):
    def test_cited_brief_is_distinct_from_summary_and_from_answer_accuracy(self):
        def response(facts, error=False):
            return {"message": {"content": [{"type": "tool_result", "is_error": error, "content": json.dumps({
                "report": {"format": "paged-report-v1", "status": "Resolved", "reportRef": "r"},
                "brief": {"format": "procedure-brief-v1", "facts": facts}})}]}}
        self.assertEqual(["BriefResolved", "PagedResolved"], report.analysis_statuses([response([{"citation": "p.sql:1"}])]))
        for facts in ([], None, [{"detail": "no citation"}], [{"citation": ""}]):
            self.assertEqual(["PagedResolved"], report.analysis_statuses([response(facts)]))
        self.assertEqual([], report.analysis_statuses([response([{"citation": "p.sql:1"}], True)]))

    def result(self, data):
        return {"message": {"content": [{"type": "tool_result", "content": json.dumps({"report": data})}]}}

    def test_summary_is_not_miscounted_as_supported_body_analysis(self):
        summary = self.result({"format": "paged-report-v1", "status": "Resolved", "reportRef": "r"})
        self.assertEqual(["PagedResolved"], report.analysis_statuses([summary]))
        placeholder = self.result({"format": "report-page-v1", "reportRef": "r", "items": [{"path": "/modules/0/calls", "expand": "/modules/0/calls"}]})
        self.assertEqual(["PagedResolved"], report.analysis_statuses([summary, placeholder]))
        evidence = self.result({"format": "report-page-v1", "reportRef": "r", "items": [{"path": "/modules/0/calls/0", "value": {"name": "dbo.Q"}}]})
        self.assertEqual(["PagedResolved", "ExpandedResolved"], report.analysis_statuses([summary, placeholder, evidence]))

    def test_legacy_and_compact_statuses_are_preserved(self):
        self.assertEqual(["Resolved", "Unresolved"], report.analysis_statuses([
            self.result({"status": "Resolved", "modules": []}),
            self.result({"format": "compact-v1", "status": "Unresolved", "modules": []})]))


if __name__ == "__main__":
    unittest.main()
