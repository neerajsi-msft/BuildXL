# ckati DirectGraph v3 conformance corpus

From the BuildXL worktree, run:

```
python3 Public/Src/FrontEnd/UnitTests/DirectGraph/Fixtures/KatiV3/regenerate.py /path/to/non-live/kati/ckati
```

The script writes extraction inputs beneath this worktree's `Out`, invokes
ckati without executing recipes, and checks in only its v3 output and expected
environment values. It normalizes the evaluation root to
`/directgraph-conformance` and action hashes to stable encounter-order names.
It does not rewrite payloads or expression structure.

The corpus covers leading/trailing TABs, CR, embedded/trailing LF, one-space and
empty values, dollars/quotes/backslashes, UTF-8 C1 controls, empty/space shell
arguments, deferred recipe shells, a long inline concat, grouped outputs, config
`process` options/semaphores/automatic temp, and graph-selected partial
installation seals with a JSON membership write.
