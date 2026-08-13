-- Covering index for the /vahter_report single-scan window query (DbService.GetReportStats):
-- one scan of `WHERE created_at >= @now - interval '7 days'`, with every column the query's
-- FILTER clauses need (chat_id, bot_auto_deleted, vahter_verdict, spam_status) included so the
-- scan never has to visit the heap. Validated on prod pre-index: 89.8ms for the equivalent
-- query, single row, correct live numbers.
--
-- Plain CREATE INDEX (not CONCURRENTLY): matches every existing migration in this bot, and the
-- brief write-lock during deploy is an accepted trade-off at this bot's ~2 msg/min volume.
CREATE INDEX idx_snapshot_message_created_at_report
    ON snapshot_message (created_at)
    INCLUDE (chat_id, bot_auto_deleted, vahter_verdict, spam_status)
    WHERE created_at IS NOT NULL;
