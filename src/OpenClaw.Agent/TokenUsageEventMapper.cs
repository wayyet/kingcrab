using OpenClaw.Core.Models;
using OpenClaw.TokenHubSink.Observability;

namespace OpenClaw.Agent;

/// <summary>
/// Maps one turn's <see cref="TurnTokenUsageRecord"/> onto a <see cref="SessionTokenUsageEvent"/> for the
/// TokenHub thin client. Only the whitelisted incremental counts (this call, safe to SUM downstream) plus the
/// <c>session_total_*</c> reconciliation snapshot cross the wire; record-only fields (<c>CacheWriteTokens</c> /
/// <c>IsEstimated</c> / <c>EstimatedInputTokensByComponent</c>) are deliberately not represented on the event
/// type, so they can never leak. AgentId prefers a configured fixed id, else the record's sender identity.
/// Side-effect free so it can be unit-tested alone.
/// </summary>
internal static class TokenUsageEventMapper
{
    public static SessionTokenUsageEvent Create(TurnTokenUsageRecord record, string? fixedAgentId)
    {
        var agentId = string.IsNullOrEmpty(fixedAgentId) ? record.SenderId : fixedAgentId;

        return new SessionTokenUsageEvent
        {
            AgentId = agentId,
            SessionId = record.SessionId,
            ChannelId = record.ChannelId,
            ProviderId = record.ProviderId,
            ModelId = record.ModelId,
            InputTokens = record.InputTokens,
            OutputTokens = record.OutputTokens,
            CacheReadTokens = record.CacheReadTokens,
            TotalTokens = record.InputTokens + record.OutputTokens,
            SessionTotalInputTokens = record.SessionTotalInputTokens,
            SessionTotalOutputTokens = record.SessionTotalOutputTokens,
            SessionTotalCacheReadTokens = record.SessionTotalCacheReadTokens,
            SessionTotalTokens = record.SessionTotalTokens
        };
    }
}
