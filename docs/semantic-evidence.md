# Semantic evidence and remaining unknowns

Core/LSP full DTOs and compact MCP reports share explicit source observations. Facts describe saved source, not a successful execution or calibrated confidence score. Relevant expressions and source ranges remain available through bounded evidence pages.

`controlFlow.nodes[].throw` names `kind` (`NewError`/`Rethrow`), `errorNumber`, `message` and **state**, each with its own source expression/range. State is not severity. A new THROW uses severity 16; bare THROW preserves the caught exception. A `return-after-throw` issue marks only a directly following RETURN in the same statement list. It does not solve arbitrary reachability or flag a RETURN after a conditional IF/THROW as necessarily unreachable. These semantics follow [Microsoft's THROW reference](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/throw-transact-sql?view=sql-server-ver17).

`observations` can report a string beginning module-definition DDL. Such text does not become an immediate call edge of its containing procedure. The classifier checks a bounded literal prefix; it does not evaluate variables, execute dynamic SQL or claim that deployment succeeds. Comments, concatenation or other generated text can remain unclassified with the existing dynamic-SQL limitation.

`SelectAssignment` preserves the distinction between an ordinary zero-row assignment retaining its previous value and a scalar-subquery no-value result. Row cardinality, repeated loop values and iteration outcomes remain unknown. See [SELECT variable assignment](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/select-local-variable-transact-sql?view=sql-server-ver17). Unknown row contents never become a known NULL or a guaranteed count of distinct fulfilled orders.

`SequenceAllocation` identifies NEXT VALUE FOR and, when observed, whether it textually precedes the first explicit BEGIN TRAN. This is not an ambient-transaction test. Sequence consumption survives rollback; concrete values/gaps depend on runtime state. See [sequence semantics](https://learn.microsoft.com/sql/relational-databases/sequence-numbers/sequence-numbers).

`AtomicTransaction` warns that declared isolation must be read from source and ambient session transactions can remain active. Atomic blocks can use SNAPSHOT, REPEATABLE READ or SERIALIZABLE. Block failure and transaction-dooming failure differ; not every caught error proves the entire outer transaction rolled back. See [atomic-block transactions](https://learn.microsoft.com/en-us/sql/relational-databases/in-memory-oltp/atomic-blocks-in-native-procedures?view=sql-server-ver17).

External methods use `implementationKind="External"`, `bodyAvailable=false` and explicit implementation observations/issues. LSP call coverage marks their body unavailable. SQL signatures remain visible, but empty SQL lists cannot establish CLR headers, network behavior or table effects. Trigger/history/temporal and other implicit effects remain unknown unless separately verified; no new trigger-binding completeness claim is made.

`OpenJsonColumn` retains case-exact column/path literals, AS JSON and BIN2 key comparison independently of SQL identifier rules. Strict/lax missing-property behavior is described without executing JSON or inventing lineage. See [OPENJSON](https://learn.microsoft.com/en-us/sql/t-sql/functions/openjson-transact-sql?view=sql-server-ver17). Percentage expressions retain their literal formula and exact range; no business-intent correction replaces actual code.

Deterministic regressions test extraction, negative conditional-flow cases, compact/full citation parity and LSP external-body coverage. Answer correctness is graded separately: correct facts cannot guarantee correct model interpretation.
