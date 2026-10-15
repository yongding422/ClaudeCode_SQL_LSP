# Public value-tracing evaluation tasks

The six questions in `samples/value-tracing-evaluation/tasks.json` extend the original single smoke task. They are authored acceptance examples with source-reviewed assertions below, **not independently reviewed benchmark results**. Keep this file outside copied model workspaces. The runner copies only `.sql` files; never add gold assertions to SQL comments or prompts. At least 30 representative procedures and three paired repeats, with independent accuracy review, remain required before a general value claim.

All citations below are one-based lines in `samples/value-tracing-evaluation/modules.sql`.

| Task | Expected observations | Claims to reject |
| --- | --- | --- |
| value-branch-rename | RoutePayroll entry (1), Region initialized from State (3), conditional reassignment to CA (4), Region passed to ApplyRegion (5); callee formals (8) and parameter reads in WHERE (10). Sources at the call include the declaration and conditional assignment. | Region always equals State or CA; branch alternatives establish correlated paths or a particular row set. |
| value-defaults | Calls (15,16) bind State to Region; Mode default 7 comes from the callee declaration (8). First selects ExplicitDefault, second OmittedDefault. The callee's symbolic input is separate from its default. | Every ApplyRegion invocation has Mode=7 regardless of supplied arguments; binding proves SQL conversions or execution. |
| value-output-boundary | Result initialized from Seed (21). Both arguments at call (23) read pre-call Result; Calculate formals (27) identify input/output roles. Output may copy back an unknown value; Status is a separate unknown return result. Callee assignment (29) and RETURN 42 (30) are observations. | Static binding proves post-call Result=Seed or Status=42, or connects an arbitrary callee exit definition to copy-back. |
| value-select-retention | Declaration (35), SELECT assignment (36), later IF read (37). Zero rows can retain a prior value; row/order behavior and actual contents remain unknown. | Result is always Eligibility from a unique or last known row; lexical order proves the runtime value. |
| value-loop-backedge | Declaration (42), loop predicate (43), read/BREAK (45), assignments (46,47), return (49). The analyzer does not solve iterations/backedges, even where a human may reason further from this small source. | A first-iteration definition is a proven source for all iterations; loop-exit values or execution are analyzer facts. |
| value-invalid-binding | InvalidRoute call uses Wrong (54), while ApplyRegion declares Region and Mode (8). BindingStatus is Unsupported and no partial formal binding is published. | Wrong silently maps to Region or a successful execution is assumed. |

Score behavior assertions, dependency precision/recall, exact citation support, explicit unknown handling and correction effort independently from runtime/token metrics. Record overstatements and one-line citation errors even when tool calls succeed. Multiple plausible source alternatives are not multiple proven runtime values.

Run the existing evaluator against `samples/value-tracing-evaluation`, passing its task file and a built/installed 0.9.0 plugin. Freeze the model and effort, retain failures, and distinguish cold/warm conditions. `--keep-answers` / `--keep-transcripts` explicitly retain local source-bearing evidence. Timing, tool-call counts and reported cost alone cannot score answer accuracy. See [measurement design](../stored-procedure-understanding.md#measuring-value-against-grep).
