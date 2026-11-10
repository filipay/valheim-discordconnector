# Repository workflow

- Inspect `git status` before editing; preserve existing work. Never discard changes to make the checkout appear clean.
- Put temporary plans, logs, snapshots and research scratch files under the ignored `.superpowers/sdd/` workspace, not the repository root. Keep durable design documents under `docs/superpowers/` and reusable research under `docs/research/`.
- Keep generated DLLs, archives, local SDKs and game references out of commits. Testing bundles belong in ignored `artifacts/server-leaderboards/`, with checksums, dependency license and provenance.
- Finish requested work with focused checks and a status summary. Commit verified changes only with explicit authorization; otherwise clearly report the remaining uncommitted files. Pushes, merges and deployments require separate authorization.
- Before a testing DLL build, read the latest `docs/server-statistics-audit.md` and `build/litedb-wal-backport/README.md`. Use the established isolated build recipe: ordinary publication targets can overwrite plugin/package destinations. Preserve the explicit approved LiteDB override when that backport is requested; do not imply an ordinary build includes it.
- Never treat stub/metadata checks as live Valheim, Harmony, crossplay or Discord verification. Do not touch production databases or delete WAL files without explicit authorization.
