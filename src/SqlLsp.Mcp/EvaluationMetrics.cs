using System.Globalization;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace SqlLsp.Mcp;

// Explicit import of the existing client runner's numeric CSV, never an inference from report bytes.
internal static class EvaluationMetrics
{
    private static readonly string[] Arms = ["grep", "plugin", "codebase-memory", "sql-mcp", "sql-mcp-lsp"];
    private static readonly string[] Measures = ["elapsed_ms", "input_tokens", "output_tokens", "cache_read_tokens", "cache_write_tokens", "reported_cost_usd", "behavior_score", "citation_score", "dependency_precision", "dependency_recall", "unsupported_handling_score"];
    private sealed record Row(string Task, string Repeat, string Arm, bool Success, Dictionary<string, string> Fields)
    {
        internal double? Number(string name) => Fields.TryGetValue(name, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            double.IsFinite(value) && value is >= 0 and <= 1e15 && (!name.EndsWith("tokens", StringComparison.Ordinal) || Math.Truncate(value) == value) &&
            (!name.EndsWith("score", StringComparison.Ordinal) && name is not ("dependency_precision" or "dependency_recall") || value <= 1) ? value : null;
    }

    internal static object Read(string path)
    {
        LocalMetricStore.CheckPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        const int maximum = 4 * 1024 * 1024;
        var bytes = new byte[maximum + 1]; var length = 0;
        while (length < bytes.Length) { var read = stream.Read(bytes.AsSpan(length)); if (read == 0) break; length += read; }
        if (length > maximum) throw new ArgumentException("Evaluation CSV exceeds limit.");
        using var parser = new TextFieldParser(new StringReader(Encoding.UTF8.GetString(bytes, 0, length).TrimStart('\uFEFF')))
        { TextFieldType = FieldType.Delimited, Delimiters = [","], HasFieldsEnclosedInQuotes = true };
        var header = parser.ReadFields() ?? throw new ArgumentException("Missing evaluation CSV header.");
        if (header.Length > 128 || header.Distinct(StringComparer.Ordinal).Count() != header.Length || !new[] { "task_id", "repeat", "arm" }.All(header.Contains))
            throw new ArgumentException("Invalid evaluation CSV header.");
        var rows = new List<Row>(); var invalid = 0; var readRows = 0;
        while (!parser.EndOfData)
        {
            if (++readRows > 10000) throw new ArgumentException("Evaluation CSV row limit exceeded.");
            string[] fields;
            try { fields = parser.ReadFields()!; }
            catch (MalformedLineException) { invalid++; continue; }
            if (fields == null || fields.Length != header.Length) { invalid++; continue; }
            var data = header.Zip(fields).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal);
            if (!Arms.Contains(data["arm"], StringComparer.Ordinal) || data["task_id"].Length is < 1 or > 256 ||
                !int.TryParse(data["repeat"], NumberStyles.None, CultureInfo.InvariantCulture, out var repeat) || repeat is < 1 or > 10000) { invalid++; continue; }
            rows.Add(new(data["task_id"], repeat.ToString(CultureInfo.InvariantCulture), data["arm"], data.GetValueOrDefault("outcome") == "success" && data.GetValueOrDefault("exit_code") == "0", data));
        }
        object Stats(IEnumerable<double?> values)
        {
            var available = values.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            return new { measuredRuns = available.Length, mean = available.Length == 0 ? (double?)null : available.Average(), median = MetricReports.Percentile(available, 0.5) };
        }
        var sets = rows.GroupBy(r => (r.Task, r.Repeat, r.Arm)).ToArray();
        var unique = sets.Where(g => g.Count() == 1).Select(g => g.Single()).ToArray();
        var baseline = unique.Where(r => r.Arm == "grep").ToDictionary(r => (r.Task, r.Repeat));
        bool SameSettings(Row c, Row b)
        {
            var keys = new[] { "model", "effort", "repo_revision", "cli_version", "start_condition", "sql_files" };
            if (!keys.All(k => !string.IsNullOrWhiteSpace(c.Fields.GetValueOrDefault(k)) && c.Fields.GetValueOrDefault(k) == b.Fields.GetValueOrDefault(k))) return false;
            return c.Fields.GetValueOrDefault("model_observed", "") == b.Fields.GetValueOrDefault("model_observed", "");
        }
        var paired = unique.Where(r => r.Arm != "grep" && baseline.ContainsKey((r.Task, r.Repeat))).Select(r => (Current: r, Baseline: baseline[(r.Task, r.Repeat)])).ToArray();
        var matching = paired.Where(p => SameSettings(p.Current, p.Baseline)).ToArray();
        var complete = matching.Where(p => p.Current.Success && p.Baseline.Success).ToArray();
        return new
        {
            format = "sqlscriptdom-client-evaluation-v1",
            provenance = "Explicit client evaluation CSV, not measurements inferred by the SQL server",
            qualityGate = "Not evaluated automatically; reviewer scores and task/source parity need independent assessment",
            validRows = rows.Count,
            invalidRows = invalid,
            duplicatePairKeys = sets.Count(g => g.Count() != 1),
            arms = rows.GroupBy(r => r.Arm).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new
            {
                arm = g.Key,
                runs = g.Count(),
                successfulRuns = g.Count(r => r.Success),
                failedOrIncompleteRuns = g.Count(r => !r.Success),
                measures = Measures.ToDictionary(m => m, m => Stats(g.Select(r => r.Number(m))))
            }).ToArray(),
            comparisonsWithGrep = complete.GroupBy(p => p.Current.Arm).Select(g => new
            {
                arm = g.Key,
                successfulMatchedPairs = g.Count(),
                reductions = Measures.Where(m => !m.EndsWith("score", StringComparison.Ordinal) && m is not ("dependency_precision" or "dependency_recall")).ToDictionary(m => m, m => Stats(g.Select(p =>
                    p.Current.Number(m) is { } c && p.Baseline.Number(m) is > 0 and var b ? (double?)(1 - c / b) : null)))
            }).ToArray(),
            unmatchedOrIncomparableNonBaselineRuns = rows.Count(r => r.Arm != "grep") - complete.Length,
            settingMismatchPairs = paired.Length - matching.Length,
            failedOrIncompleteMatchedPairs = matching.Length - complete.Length,
            limitations = "Descriptive paired ratios only, not proof of superiority or statistical significance. Duplicate pair keys excluded from pairing; failures remain in arm totals. Missing/invalid numbers stay null. Token cache categories remain separate; provider billing differs. Score fields are supplied by external reviewers and are not generated here. Match task source visibility and independently review correctness; recorded settings alone do not establish a controlled experiment."
        };
    }
}
