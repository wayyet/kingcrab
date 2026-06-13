-- Doris-side setup for session token usage analytics.
-- Source of truth: Session-Token用量Kafka推送与Doris汇聚统计-设计文档.md §7
-- Pipeline: Kafka topic `session-token-metrics` -> Routine Load -> detail table -> async MV.

CREATE DATABASE IF NOT EXISTS token_metrics;

-- 1) Detail table (Duplicate model, daily dynamic partitions, 90-day retention).
CREATE TABLE token_metrics.session_token_events (
    event_time           DATETIME(3)   NOT NULL COMMENT "事件时间(UTC)",
    agent_id             VARCHAR(64)   NOT NULL COMMENT "数字员工ID",
    session_id           VARCHAR(128)  NOT NULL COMMENT "会话ID",
    event_id             VARCHAR(36)   NOT NULL COMMENT "事件UUID(去重用)",
    channel_id           VARCHAR(64)            COMMENT "渠道",
    provider_id          VARCHAR(64)            COMMENT "LLM服务商",
    model_id             VARCHAR(128)           COMMENT "模型",
    input_tokens         BIGINT        NOT NULL DEFAULT "0",
    output_tokens        BIGINT        NOT NULL DEFAULT "0",
    cache_read_tokens    BIGINT        NOT NULL DEFAULT "0",
    total_tokens         BIGINT        NOT NULL DEFAULT "0",
    session_total_tokens BIGINT        NOT NULL DEFAULT "0" COMMENT "会话累计快照(对账用，禁止SUM)"
)
DUPLICATE KEY(event_time, agent_id, session_id)
PARTITION BY RANGE(event_time) ()
DISTRIBUTED BY HASH(agent_id) BUCKETS 10
PROPERTIES (
    "dynamic_partition.enable"    = "true",
    "dynamic_partition.time_unit" = "DAY",
    "dynamic_partition.start"     = "-90",
    "dynamic_partition.end"       = "3",
    "dynamic_partition.prefix"    = "p",
    "replication_num"             = "3"
);

-- 2) Routine Load: Doris consumes Kafka directly (At-Least-Once); no custom consumer needed.
CREATE ROUTINE LOAD token_metrics.load_session_token_events
ON session_token_events
COLUMNS(event_time, agent_id, session_id, event_id, channel_id,
        provider_id, model_id, input_tokens, output_tokens,
        cache_read_tokens, total_tokens, session_total_tokens)
PROPERTIES (
    "format"                    = "json",
    "jsonpaths"                 = "[\"$.event_time\",\"$.agent_id\",\"$.session_id\",\"$.event_id\",\"$.channel_id\",\"$.provider_id\",\"$.model_id\",\"$.input_tokens\",\"$.output_tokens\",\"$.cache_read_tokens\",\"$.total_tokens\",\"$.session_total_tokens\"]",
    "desired_concurrent_number" = "3",
    "max_batch_interval"        = "10",
    "max_error_number"          = "1000"
)
FROM KAFKA (
    "kafka_broker_list"              = "kafka-1:9092,kafka-2:9092,kafka-3:9092",
    "kafka_topic"                    = "session-token-metrics",
    "property.group.id"              = "doris-token-loader",
    "property.kafka_default_offsets" = "OFFSET_END"
);

-- 3) Async materialized view: per-agent daily rollup, refreshed every 5 minutes.
CREATE MATERIALIZED VIEW token_metrics.agent_token_usage_daily
BUILD IMMEDIATE REFRESH ASYNC EVERY (INTERVAL 5 MINUTE)
DISTRIBUTED BY HASH(agent_id) BUCKETS 10
AS
SELECT
    agent_id,
    DATE(event_time)       AS stat_date,
    COUNT(*)               AS llm_calls,
    SUM(input_tokens)      AS input_tokens,
    SUM(output_tokens)     AS output_tokens,
    SUM(cache_read_tokens) AS cache_read_tokens,
    SUM(total_tokens)      AS total_tokens
FROM token_metrics.session_token_events
GROUP BY agent_id, DATE(event_time);
