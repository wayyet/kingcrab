using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.TokenHubSink.Observability;

namespace OpenClaw.Agent.Observability;

/// <summary>
/// TokenHub bypass as a member of the turn token-usage observer chain: maps each turn's record to one
/// incremental <see cref="SessionTokenUsageEvent"/> and enqueues it on the thin-client sink for the
/// out-of-sandbox collector. Only the whitelisted fields cross the wire (see <see cref="TokenUsageEventMapper"/>).
/// <para>
/// Publish only enqueues into a bounded in-memory channel — never blocking the LLM hot path — and the
/// no-op sink short-circuits before allocating, so a disabled TokenHub still costs nothing. The composite
/// observer wraps every member in try/catch, so a failure here cannot disturb auditing or provider accounting.
/// </para>
/// </summary>
public sealed class TokenHubSinkTurnTokenUsageObserver : ITurnTokenUsageObserver
{
    private readonly ITokenUsageEventSink _sink;
    private readonly string? _fixedAgentId;

    public TokenHubSinkTurnTokenUsageObserver(ITokenUsageEventSink sink, string? fixedAgentId)
    {
        _sink = sink;
        _fixedAgentId = fixedAgentId;
    }

    public void RecordTurn(TurnTokenUsageRecord record)
    {
        if (_sink is NullTokenUsageEventSink)
            return;

        _sink.Publish(TokenUsageEventMapper.Create(record, _fixedAgentId));
    }
}
