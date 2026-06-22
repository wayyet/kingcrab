using OpenClaw.Core.Models;
using OpenClaw.TokenHubSink.Observability;

namespace OpenClaw.Agent;

/// <summary>
/// Maps one LLM call's usage onto a <see cref="SessionTokenUsageEvent"/> for the TokenHub thin client.
/// Incremental fields carry this call's counts (safe to SUM downstream); the <c>session_total_*</c> fields
/// are a snapshot of the running session totals (reconciliation only, never SUM). AgentId prefers a
/// configured fixed id, else the session sender identity. Side-effect free so it can be unit-tested alone.
/// </summary>
internal static class TokenUsageEventMapper
{
    public static SessionTokenUsageEvent Create(
        Session session,
        string? fixedAgentId,
        string providerId,
        string modelId,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens)
    {
        var agentId = string.IsNullOrEmpty(fixedAgentId) ? session.SenderId : fixedAgentId;

        return new SessionTokenUsageEvent
        {
            AgentId = agentId,
            SessionId = session.Id,
            ChannelId = session.ChannelId,
            ProviderId = providerId,
            ModelId = modelId,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = cacheReadTokens,
            TotalTokens = inputTokens + outputTokens,
            SessionTotalInputTokens = session.TotalInputTokens,
            SessionTotalOutputTokens = session.TotalOutputTokens,
            SessionTotalCacheReadTokens = session.TotalCacheReadTokens,
            SessionTotalTokens = session.GetTotalTokens()
        };
    }
}
