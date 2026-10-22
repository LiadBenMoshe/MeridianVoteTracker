# Decisions

## 1. Main decisions and trade-offs

- **The backend owns the rules.** The ingestion engine (`Meridian.Core`) is pure C# with no database, so the §7.2 example is unit-tested row by row. The API and workers only load data and save results.
- **Data access: EF Core + Npgsql.** EF Core handles the model, migrations and normal queries. For bulk writes I use raw Npgsql binary `COPY` (country votes and the per-row report). This takes the 962k-row history import from "hours" to about 2 minutes.
- **Background jobs: a queue table in PostgreSQL** (`status` column, claimed with `FOR UPDATE SKIP LOCKED`). It needs no extra infrastructure. Jobs and their progress survive restarts and page refreshes. Imports run one at a time, so two files never race on the same vote.
- **Each chunk of 250 votes is one transaction.** A crash leaves consistent data. An interrupted import is marked *Failed* rather than re-run silently, because a re-run would report the earlier rows as "unchanged". Re-uploading is always safe.
- **Simplified login.** The UI sends `X-User`. Roles come from the `users` table and are enforced with `[Authorize]` policies (analyst → 403 on admin endpoints).
- **One error format.** Every error is RFC 7807 ProblemDetails with a `correlationId`. Validation errors are returned per field. A conflict that someone else already decided returns 409 with the current state.

## 2. Ingestion rules I chose (open points)

- **Date format.** ING-04 says `dd-mm-yyyy`, but Appendix A and all the files use `yyyy-mm-dd`. I accept both and store ISO. `21/02/2024` is rejected.
- **Normalisation is reported, never silent.** I trim spaces, upper-case country codes (`deu` → `DEU`) and collapse double spaces in titles. Each change appears in the report with its row number. `Yes`, `0` and empty ballots are rejected, not guessed.
- **Line = record.** Each line is parsed alone, so a broken quote only kills its own line (ING-07).
- **Vote details that differ inside one file** (e.g. a title with " (draft)") make the whole vote a *contradiction*: the system does not pick a title. The corrected file then brings the vote in as new.
- **A conflict is not reopened** if one is pending with the same incoming value, or if a human already decided between exactly these two values. Re-uploading either file after a decision therefore creates 0 conflicts.
- **What I found in the data:** in Q1 there are 11 invalid rows (unknown code `FRN`, `Yes`, an empty ballot, `AGO;N`, 6 dates like `21/02/2024`, and NZL after it left), 3 duplicates, 151 contradiction rows, a partial vote (031) and `Turkey`/`Türkiye`. Rev2 opens 8 conflicts (6 ballots, 1 title, 1 date). In the history file there are 32 invalid rows (unquoted titles with commas, ballots `0`/`?`/`YES`) and 30 duplicates.

## 3. Scoring rules (shown inside every snapshot)

Score = 100 × matching weight ÷ counted weight. **Only YES/NO are counted**: ABSTAIN and ABSENT are neutral. Votes held while a state was not a member have no ballot, so they are excluded. A state with no counted vote gets **n/a** and no rank. Equal scores share a rank. A vote without a position **blocks** snapshot creation (422 with the list of votes). It is never skipped silently. The rules are stored with the snapshot (version `v1`).

## 4. Database and history

- `country_votes` holds the **current approved** value and its source (import or conflict decision).
- `vote_events` is an **append-only** log of everything that happens to a vote: created, countries added, conflicts created, accepted/rejected (old → new, file, who), position/weight changes (old → new) and publishing. It powers the vote timeline.
- `imports` + `import_rows` + `import_votes` keep each upload's file (in Blob), uploader, time, counts and the outcome of **every row**.
- `conflicts`: a partial unique index stops the same pending change being opened twice. A decision is one conditional `UPDATE … WHERE status = 'Pending'`, so exactly one of two parallel decisions wins.
- **Snapshots freeze their inputs.** `snapshot_votes` stores the position and weight, `snapshot_ballots` the approved ballots, and `snapshot_scores` the results. They are never recalculated, so they open the same way a year later.
- **DB triggers** block UPDATE and DELETE on the log, report and snapshot tables, block deleting approved votes, and block changing a decided conflict. Check constraints cover ballots, weight 1–5 and the quarter format.

## 5. Azure resources

| Used | Why |
|---|---|
| Azure Database for PostgreSQL | The system database (connection string from config). |
| Blob Storage | Original CSV/PDF files, so every import can be re-opened. |
| AI Document Intelligence (`prebuilt-layout`) | Bonus: it reads the sheet's table with confidence per word. The parsing and domain checks are my own code. |
| Application Insights | Central logs and requests, tagged with `CorrelationId`. |
| Key Vault | Optional source for all secrets (`KeyVault__Uri`). |
| Microsoft Entra ID | **No keys at all in the dev setup.** Blob, Document Intelligence and PostgreSQL use the developer's own Entra identity (RBAC roles: Storage Blob Data Contributor, Cognitive Services User, PostgreSQL Entra admin). In Docker this is one device-code sign-in at startup, cached in a volume. In production it would be a managed identity. |

**Not used:** *Azure OpenAI and Claude (Foundry)*. The sheets are fixed tables, so layout + rules is cheaper, deterministic and gives per-cell confidence; an LLM adds cost and new failure modes. *Cosmos DB*: the data is relational with strong consistency needs.

**Correlation ID:** the UI creates `X-Correlation-Id`. The API puts it in every log line and saves it on the job row. The worker restores it and sends it to Document Intelligence as a header.

## 6. Bonus: measuring extraction quality

1. Hand-label the 6 sheets (≈900 cells) once as ground truth.
2. Measure cell accuracy, accuracy of "absent" (empty rows), and how many errors are *caught* by checks (totals mismatch, unknown or duplicate code, low confidence) versus missed.
3. Track the analyst edit rate per sheet in production: every edit is a measured error.

The goal is not 100% accuracy. The goal is that no wrong value passes review without a flag.

**First real run (6 sheets, Azure Document Intelligence):**
- 3 sheets were perfect: 148 rows, totals match, 0 issues.
- 3 sheets were flagged correctly: ANT (not a member), Panama twice with different values, double or unclear marks, and a noisy scan with codes like `MW.I`. Noisy codes get a suggested clean code but stay flagged for a human.
- The corrigendum of MA/2011/042 opened exactly 1 conflict (KEN YES → ABSTAIN), with no silent overwrite.

## 7. Before production

- Real identity (Entra ID) instead of `X-User`, with managed identities for Azure (no keys).
- Move the workers to a separate process or container, and run imports in parallel per vote with advisory locks.
- Partition `import_rows` and `snapshot_ballots`, and set a retention policy for raw rows.
- Add rate limits and a malware scan on uploads, alerts on failed jobs, and backup and restore drills.
- Add end-to-end UI tests (Playwright) and contract tests on the generated client.

## 8. Next phase: 4 developers

| Dev | Area |
|---|---|
| 1 | Ingestion engine + data-quality rules (owner of `Meridian.Core`) |
| 2 | Conflicts, history, audit, permissions |
| 3 | Snapshots and scoring, performance at scale |
| 4 | Frontend + the API contract (generated client), UX of the review flows |

**Standards first:** the OpenAPI contract is the source of truth and the client is generated. Errors use ProblemDetails. Every schema change is a migration. Each business rule is a test named after its requirement ID. PRs need a review and green CI (unit + integration with real PostgreSQL). No secrets in code. Logs carry the correlation ID.
