#!/usr/bin/env bash
# Creates the Kafka topic consumed by Doris Routine Load for session token usage events.
# See docs: Session-Token用量Kafka推送与Doris汇聚统计-设计文档.md §6
set -euo pipefail

BOOTSTRAP="${KAFKA_BOOTSTRAP:-kafka-1:9092}"

kafka-topics.sh --bootstrap-server "$BOOTSTRAP" --create \
  --topic session-token-metrics \
  --partitions 6 \
  --replication-factor 3 \
  --config retention.ms=259200000 \
  --config min.insync.replicas=2
