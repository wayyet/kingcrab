using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface ISessionMetadataStore
{
    SessionMetadataSnapshot Get(string sessionId);
    IReadOnlyDictionary<string, SessionMetadataSnapshot> GetAll();
    SessionMetadataSnapshot Set(string sessionId, SessionMetadataUpdateRequest request);
}
