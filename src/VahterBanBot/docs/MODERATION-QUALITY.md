# Historical moderation quality

`moderation_quality_daily` stores correction-based estimates, not independently audited ground truth. Uncorrected decisions are assumed correct; missed spam that nobody reports remains invisible.

## Populations

| Cohort | System | Meaning |
| --- | --- | --- |
| `all_scored` | `pipeline` | Actual ML + LLM automatic deletion, once per ML-scored message |
| `all_scored` | `ml` | Counterfactual ML alone, SPAM when score > 0, HAM otherwise |
| `llm_triaged` | `ml` | Counterfactual ML on the same messages as the paired LLM row |
| `llm_triaged` | `llm` | Recorded LLM verdict, grouped by recorded deployment name |

Filter both cohort and system before summing. The populations overlap: adding systems or cohorts double-counts messages. In the paired cohort, `llm_model` identifies the partner LLM deployment for both systems; NULL means the historical event has no model attribution. ML model versions are not recorded.

The paired cohort requires an LLM event and its preceding ML score in `[-0.5, 1.5)`; 1.5 is the direct-ML spam boundary. It includes cached LLM verdicts. SKIP contributes `abstained` to both paired rows, keeping the precision/recall populations identical.

The UTC day comes from the first ML score. Edits do not create a second message. The latest score and LLM verdict before the latest human correction are evaluated. A text change between evaluation and correction excludes that comparison.

Latest decisive human correction wins across message labels, reviewer actions, and human unbans. An unban corrects only the message targeted by that user's preceding ban. Without a correction, automatic deletion implies SPAM, otherwise the current LLM verdict supplies the reference, otherwise HAM is assumed.

Whole-pipeline rows measure automatic deletions, so human-caught spam without an automatic deletion is FN. Unreviewed SKIP is unresolved; reviewed SKIP can contribute to pipeline counts. Non-ML/LLM deletion reasons are excluded from whole-pipeline comparisons. Messages without an ML score, including deterministic prefilters and older uninstrumented history, are outside these populations.

## Scheduling and backfill

The existing scheduler runs at `CLEANUP_SCHEDULED_HOUR_UTC`. Every daily run replaces today and the previous 14 UTC days, plus older affected days. Today is partial as of `computed_at`; recent days are provisional because reviewers can still correct decisions.

An event-table insert trigger atomically enqueues relevant message, scoring, moderation, and unban events. The job resolves their original scoring days, persists them in `moderation_quality_dirty_day`, and acknowledges only the pending event IDs it read, in one transaction. Late commits remain pending regardless of event-ID order.

Each day uses a repeatable-read transaction to replace its rows and remove its dirty marker together. New events during rebuilding remain pending for the next run. Failed or interrupted rebuilds leave dirty days available for retry; older edits and corrections are covered without a maximum age.

Authenticated `POST /quality-history` (or `?mode=daily`) runs a refresh. `POST /quality-history?day=YYYY-MM-DD` replaces one day up to and including today. A session advisory lock serializes scheduled, backfill, and manual jobs across pods.

One-time backfill is explicit: `POST /quality-history?mode=backfill` fills missing days before the rolling window, oldest first. `QUALITY_BACKFILL_DAYS` defaults to 7 and is capped at 31 per invocation. Repeat until `completedDays` is zero; completed and empty days are persisted, so retries resume safely. Daily refresh does not run this backfill.

Structured completion logs include mode, completed-day count, and elapsed seconds. Daily refresh warns when it exceeds the 30-second performance budget; it continues to finish its work. Dashboard queries read the day-indexed aggregate table and never replay events.

## Dashboard SQL

Use half-open UTC dates, `$1` inclusive and `$2` exclusive. Parameters are bound by the dashboard/query client. Return NULL for empty denominators.

```sql
SELECT day, day >= CURRENT_DATE - 14 AS provisional, max(computed_at) AS computed_at,
       sum(tp) AS tp, sum(tn) AS tn, sum(fp) AS fp, sum(fn) AS fn,
       100.0 * sum(tp) / nullif(sum(tp + fp), 0) AS precision_pct,
       100.0 * sum(tp) / nullif(sum(tp + fn), 0) AS recall_pct,
       sum(abstained) AS abstained, sum(unresolved) AS unresolved,
       sum(excluded) AS excluded
FROM moderation_quality_daily
WHERE day >= $1::date AND day < $2::date
  AND cohort = 'all_scored' AND system = 'pipeline'
GROUP BY day
ORDER BY day;
```

For an interval, remove `day`, replace the provisional expression with `bool_or(day >= CURRENT_DATE - 14)`, and remove GROUP BY/ORDER BY. Sum counts first; do not average daily percentages or multiply ML and LLM precision.

```sql
SELECT system, llm_model,
       sum(tp) AS tp, sum(tn) AS tn, sum(fp) AS fp, sum(fn) AS fn,
       100.0 * sum(tp) / nullif(sum(tp + fp), 0) AS precision_pct,
       100.0 * sum(tp) / nullif(sum(tp + fn), 0) AS recall_pct,
       sum(abstained) AS abstained, sum(unresolved) AS unresolved,
       sum(excluded) AS excluded
FROM moderation_quality_daily
WHERE day >= $1::date AND day < $2::date AND cohort = 'llm_triaged'
GROUP BY system, llm_model
ORDER BY llm_model, system;
```

For 30 days including today, bind `$1 = CURRENT_DATE - 29` and `$2 = CURRENT_DATE + 1` with the database session in UTC. For 30 complete days, use `$1 = CURRENT_DATE - 30` and `$2 = CURRENT_DATE`. Show coverage, missing days, and `computed_at`; even older days can change after edits or corrections.
