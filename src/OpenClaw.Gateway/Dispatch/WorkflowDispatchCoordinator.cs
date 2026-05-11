using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Dispatch;

internal sealed class WorkflowDispatchCoordinator
{
    private static readonly HashSet<string> OpenStatuses = new(StringComparer.Ordinal)
    {
        "drafting",
        "ready_to_dispatch",
        "dispatched",
        "dirty",
        "needs_review"
    };

    private static readonly HashSet<string> DispatchableStatuses = new(StringComparer.Ordinal)
    {
        "ready_to_dispatch",
        "dirty"
    };

    private readonly ISessionMetadataStore _metadataStore;
    private readonly ILogger<WorkflowDispatchCoordinator> _logger;
    private readonly IWorkflowDispatchRunner? _runner;

    public WorkflowDispatchCoordinator(
        ISessionMetadataStore metadataStore,
        ILogger<WorkflowDispatchCoordinator> logger,
        IWorkflowDispatchRunner? runner = null)
    {
        _metadataStore = metadataStore;
        _logger = logger;
        _runner = runner;
    }

    public void ProcessControlBlocks(Session session, IEnumerable<ControlBlock> blocks)
    {
        foreach (var block in blocks)
        {
            try
            {
                if (block.Kind == ControlBlockKind.Dispatch)
                {
                    if (!DispatchSignalParser.TryParseDispatch(block.Json, out var signal, out var parseError))
                    {
                        _logger.LogWarning("Invalid dispatch control block in session {SessionId}: {Error}", session.Id, parseError);
                        continue;
                    }

                    var result = AcceptDispatch(session, signal);
                    if (!result.Accepted)
                    {
                        _logger.LogWarning(
                            "Dispatch rejected in session {SessionId} target={Target}: {Error}",
                            session.Id,
                            signal.Target,
                            result.Error);
                    }

                    continue;
                }

                if (!DispatchSignalParser.TryParseCallback(block.Json, out var callback, out var callbackError))
                {
                    _logger.LogWarning("Invalid dispatch_callback control block in session {SessionId}: {Error}", session.Id, callbackError);
                    continue;
                }

                var callbackResult = AcceptCallback(session, callback);
                if (!callbackResult.Accepted)
                {
                    _logger.LogWarning(
                        "Dispatch callback rejected in session {SessionId} source={Source}: {Error}",
                        session.Id,
                        callback.SourceDispatchTarget,
                        callbackResult.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process dispatch control block in session {SessionId}.", session.Id);
            }
        }
    }

    public DispatchCoordinatorResult AcceptDispatch(Session session, DispatchSignal signal)
    {
        if (string.Equals(signal.Target, "stage_transition", StringComparison.Ordinal))
            return AcceptStageTransition(session, signal);

        if (!TryGetTargetShape(signal.Target, out var stage, out var targetSkill))
            return DispatchCoordinatorResult.Rejected($"Unknown dispatch target '{signal.Target}'.");

        var selectedIds = signal.HandoffIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (selectedIds.Length == 0)
            return DispatchCoordinatorResult.Rejected("dispatch.handoff_ids must contain at least one Handoff id.");

        var metadata = _metadataStore.Get(session.Id);
        var items = metadata.HandoffItems.ToList();
        var selectedSet = new HashSet<string>(selectedIds, StringComparer.Ordinal);
        var sameTargetActive = items
            .Where(item => IsActive(item) && IsTargetItem(item, stage, targetSkill))
            .ToArray();

        var blockers = sameTargetActive
            .Where(static item => item.Status is "drafting" or "dispatched" or "needs_review")
            .ToArray();
        if (blockers.Length > 0)
        {
            var blockerList = string.Join(", ", blockers.Select(static item => $"{item.HandoffId}:{item.Status}"));
            return DispatchCoordinatorResult.Rejected($"Active blockers prevent dispatch: {blockerList}.");
        }

        var dispatchableIds = sameTargetActive
            .Where(item => DispatchableStatuses.Contains(item.Status))
            .Select(static item => item.HandoffId)
            .ToArray();
        if (!selectedSet.SetEquals(dispatchableIds))
        {
            var expected = string.Join(", ", dispatchableIds);
            return DispatchCoordinatorResult.Rejected($"dispatch.handoff_ids must match all active dispatchable Handoff ids for this stage/target: {expected}.");
        }

        foreach (var handoffId in selectedIds)
        {
            var item = items.FirstOrDefault(candidate => string.Equals(candidate.HandoffId, handoffId, StringComparison.Ordinal));
            if (item is null)
                return DispatchCoordinatorResult.Rejected($"Handoff id '{handoffId}' was not found.");
            if (!string.Equals(item.SessionId, session.Id, StringComparison.Ordinal))
                return DispatchCoordinatorResult.Rejected($"Handoff id '{handoffId}' does not belong to the current session.");
            if (!IsTargetItem(item, stage, targetSkill))
                return DispatchCoordinatorResult.Rejected($"Handoff id '{handoffId}' is not a {stage}/{targetSkill} item.");
            if (!DispatchableStatuses.Contains(item.Status))
                return DispatchCoordinatorResult.Rejected($"Handoff id '{handoffId}' is not ready to dispatch; status is '{item.Status}'.");

            var payloadError = ValidatePayloadForTarget(item);
            if (payloadError is not null)
                return DispatchCoordinatorResult.Rejected($"Handoff id '{handoffId}' is not dispatchable: {payloadError}");
        }

        var selectedOriginalItems = items
            .Where(item => selectedSet.Contains(item.HandoffId))
            .ToArray();
        var dispatchId = CreateDispatchId();
        for (var index = 0; index < items.Count; index++)
        {
            if (!selectedSet.Contains(items[index].HandoffId))
                continue;

            items[index] = MarkDispatched(items[index], dispatchId);
        }

        var now = DateTimeOffset.UtcNow;
        var dispatches = metadata.DispatchItems.ToList();
        dispatches.Add(new SessionDispatchItem
        {
            DispatchId = dispatchId,
            SessionId = session.Id,
            SourceSkill = "employment-coach-conversation",
            Target = signal.Target,
            HandoffIds = selectedIds,
            Mode = signal.Mode,
            Note = signal.Note,
            To = signal.To,
            Status = "accepted",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });

        _metadataStore.Set(session.Id, new SessionMetadataUpdateRequest
        {
            HandoffItems = items,
            DispatchItems = dispatches
        });

        _logger.LogInformation(
            "Accepted dispatch {DispatchId} in session {SessionId} target={Target} handoffs={HandoffIds}",
            dispatchId,
            session.Id,
            signal.Target,
            string.Join(",", selectedIds));

        _runner?.Enqueue(new WorkflowDispatchExecutionRequest(
            session.Id,
            session.ChannelId,
            session.SenderId,
            dispatchId,
            signal.Target,
            selectedIds,
            signal.Mode,
            signal.Note,
            selectedOriginalItems));

        return DispatchCoordinatorResult.Success(dispatchId);
    }

    public DispatchCoordinatorResult AcceptCallback(Session session, DispatchCallbackSignal callback)
    {
        var callbackIds = callback.HandoffIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (callbackIds.Length == 0)
            return DispatchCoordinatorResult.Rejected("dispatch_callback.handoff_ids must contain at least one Handoff id.");

        var resultIds = callback.TodoResults
            .Select(static result => result.HandoffId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (!new HashSet<string>(callbackIds, StringComparer.Ordinal).SetEquals(resultIds))
            return DispatchCoordinatorResult.Rejected("dispatch_callback.todo_results must cover exactly every callback Handoff id.");

        var metadata = _metadataStore.Get(session.Id);
        var dispatches = metadata.DispatchItems.ToList();
        var dispatchIndex = FindMatchingDispatch(dispatches, callback.SourceDispatchTarget, callbackIds);
        if (dispatchIndex < 0)
            return DispatchCoordinatorResult.Rejected("No matching accepted dispatch was found for this callback.");

        var items = metadata.HandoffItems.ToList();
        var callbackSet = new HashSet<string>(callbackIds, StringComparer.Ordinal);
        var stale = items.Any(item => callbackSet.Contains(item.HandoffId) && string.Equals(item.Status, "dirty", StringComparison.Ordinal));
        var status = stale ? "stale" : callback.Status;
        var now = DateTimeOffset.UtcNow;

        for (var index = 0; index < items.Count; index++)
        {
            if (!callbackSet.Contains(items[index].HandoffId))
                continue;
            if (string.Equals(items[index].Status, "dirty", StringComparison.Ordinal))
                continue;

            items[index] = new SessionHandoffItem
            {
                SessionId = items[index].SessionId,
                WorkflowId = items[index].WorkflowId,
                HandoffId = items[index].HandoffId,
                Title = items[index].Title,
                Kind = items[index].Kind,
                Stage = items[index].Stage,
                TargetSkill = items[index].TargetSkill,
                Intent = items[index].Intent,
                Category = items[index].Category,
                Payload = ClonePayload(items[index].Payload),
                Source = items[index].Source,
                Acceptance = items[index].Acceptance,
                Status = items[index].Status,
                Fingerprint = items[index].Fingerprint,
                RelatedTodos = items[index].RelatedTodos,
                RelatedFiles = items[index].RelatedFiles,
                Revision = items[index].Revision + 1,
                CreatedAtUtc = items[index].CreatedAtUtc,
                UpdatedAtUtc = now,
                DispatchId = items[index].DispatchId,
                CallbackSummary = callback.UserSummary
            };
        }

        var existingDispatch = dispatches[dispatchIndex];
        dispatches[dispatchIndex] = new SessionDispatchItem
        {
            DispatchId = existingDispatch.DispatchId,
            SessionId = existingDispatch.SessionId,
            SourceSkill = existingDispatch.SourceSkill,
            Target = existingDispatch.Target,
            HandoffIds = existingDispatch.HandoffIds,
            Mode = existingDispatch.Mode,
            Note = existingDispatch.Note,
            To = existingDispatch.To,
            Status = status,
            CreatedAtUtc = existingDispatch.CreatedAtUtc,
            UpdatedAtUtc = now,
            CompletedAtUtc = now,
            CallbackSummary = callback.UserSummary,
            Errors = callback.Errors
        };

        _metadataStore.Set(session.Id, new SessionMetadataUpdateRequest
        {
            HandoffItems = items,
            DispatchItems = dispatches
        });

        _logger.LogInformation(
            "Accepted dispatch callback in session {SessionId} dispatch={DispatchId} status={Status}",
            session.Id,
            existingDispatch.DispatchId,
            status);

        return DispatchCoordinatorResult.Success(existingDispatch.DispatchId);
    }

    private DispatchCoordinatorResult AcceptStageTransition(Session session, DispatchSignal signal)
    {
        if (signal.HandoffIds.Length > 0)
            return DispatchCoordinatorResult.Rejected("stage_transition dispatch must not include handoff_ids.");
        if (string.IsNullOrWhiteSpace(signal.To))
            return DispatchCoordinatorResult.Rejected("stage_transition dispatch requires 'to'.");

        var metadata = _metadataStore.Get(session.Id);
        var dispatchId = CreateDispatchId();
        var now = DateTimeOffset.UtcNow;
        var dispatches = metadata.DispatchItems.ToList();
        dispatches.Add(new SessionDispatchItem
        {
            DispatchId = dispatchId,
            SessionId = session.Id,
            SourceSkill = "employment-coach-conversation",
            Target = signal.Target,
            HandoffIds = [],
            Mode = signal.Mode,
            Note = signal.Note,
            To = signal.To,
            Status = "accepted",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });

        _metadataStore.Set(session.Id, new SessionMetadataUpdateRequest { DispatchItems = dispatches });
        return DispatchCoordinatorResult.Success(dispatchId);
    }

    private static bool TryGetTargetShape(string target, out string stage, out string targetSkill)
    {
        stage = target switch
        {
            "ontology-extraction" => "material",
            "skill-generation" => "skill",
            "external-config" => "external",
            _ => ""
        };
        targetSkill = target;
        return stage.Length > 0;
    }

    private static bool IsActive(SessionHandoffItem item)
        => OpenStatuses.Contains(item.Status);

    private static bool IsTargetItem(SessionHandoffItem item, string stage, string targetSkill)
        => string.Equals(item.Kind, "handoff_todo", StringComparison.Ordinal)
           && string.Equals(item.Stage, stage, StringComparison.Ordinal)
           && string.Equals(item.TargetSkill, targetSkill, StringComparison.Ordinal);

    private static string? ValidatePayloadForTarget(SessionHandoffItem item)
    {
        if (string.Equals(item.Stage, "material", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(item.Category))
                return "category is required.";
            if (!PayloadHasString(item.Payload, "objective"))
                return "payload.objective is required.";
            if (!PayloadHasString(item.Payload, "scene_hint"))
                return "payload.scene_hint is required.";
            if (!PayloadHasNonEmptyStringArray(item.Payload, "source_files")
                && !PayloadHasString(item.Payload, "source_content")
                && !PayloadHasString(item.Payload, "source_summary"))
            {
                return "payload.source_files or payload.source_content/source_summary is required.";
            }
        }

        return null;
    }

    private static bool PayloadHasString(JsonElement payload, string name)
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString());

    private static bool PayloadHasNonEmptyStringArray(JsonElement payload, string name)
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Array
           && value.EnumerateArray().Any(static item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()));

    private static SessionHandoffItem MarkDispatched(SessionHandoffItem item, string dispatchId)
    {
        var revisionIncrement = string.Equals(item.Status, "dirty", StringComparison.Ordinal) ? 2 : 1;
        return new SessionHandoffItem
        {
            SessionId = item.SessionId,
            WorkflowId = item.WorkflowId,
            HandoffId = item.HandoffId,
            Title = item.Title,
            Kind = item.Kind,
            Stage = item.Stage,
            TargetSkill = item.TargetSkill,
            Intent = item.Intent,
            Category = item.Category,
            Payload = ClonePayload(item.Payload),
            Source = item.Source,
            Acceptance = item.Acceptance,
            Status = "dispatched",
            Fingerprint = item.Fingerprint,
            RelatedTodos = item.RelatedTodos,
            RelatedFiles = item.RelatedFiles,
            Revision = item.Revision + revisionIncrement,
            CreatedAtUtc = item.CreatedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            DispatchId = dispatchId,
            CallbackSummary = item.CallbackSummary
        };
    }

    private static int FindMatchingDispatch(IReadOnlyList<SessionDispatchItem> dispatches, string target, string[] handoffIds)
    {
        var set = new HashSet<string>(handoffIds, StringComparer.Ordinal);
        for (var index = dispatches.Count - 1; index >= 0; index--)
        {
            var dispatch = dispatches[index];
            if (!string.Equals(dispatch.Target, target, StringComparison.Ordinal))
                continue;
            if (!set.SetEquals(dispatch.HandoffIds))
                continue;
            return index;
        }

        return -1;
    }

    private static JsonElement ClonePayload(JsonElement payload)
        => payload.ValueKind == JsonValueKind.Undefined ? EmptyObject() : payload.Clone();

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static string CreateDispatchId()
        => $"dispatch_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..35];
}

internal readonly record struct DispatchCoordinatorResult(bool Accepted, string? DispatchId, string? Error)
{
    public static DispatchCoordinatorResult Success(string dispatchId) => new(true, dispatchId, null);
    public static DispatchCoordinatorResult Rejected(string error) => new(false, null, error);
}
