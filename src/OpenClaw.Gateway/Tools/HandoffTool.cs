using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Tools;

internal sealed class HandoffTool : IToolWithContext
{
    private readonly ISessionMetadataStore _metadataStore;
    private readonly IReadOnlyDictionary<string, HandoffWorkflowOptions> _workflows;
    private readonly string _defaultWorkflowId;

    public HandoffTool(ISessionMetadataStore metadataStore, HandoffConfig config)
        : this(metadataStore, HandoffWorkflowRegistry.FromConfig(config))
    {
    }

    public HandoffTool(ISessionMetadataStore metadataStore, HandoffWorkflowOptions workflow)
        : this(metadataStore, [workflow], workflow.WorkflowId)
    {
    }

    public HandoffTool(ISessionMetadataStore metadataStore, IEnumerable<HandoffWorkflowOptions> workflows, string? defaultWorkflowId = null)
        : this(metadataStore, new HandoffWorkflowRegistry
        {
            DefaultWorkflowId = defaultWorkflowId ?? "",
            Workflows = workflows.ToArray()
        })
    {
    }

    private HandoffTool(ISessionMetadataStore metadataStore, HandoffWorkflowRegistry registry)
    {
        _metadataStore = metadataStore;
        var workflowList = registry.Workflows;
        if (workflowList.Length == 0)
            throw new ArgumentException("At least one handoff workflow must be configured.", nameof(registry));

        foreach (var workflow in workflowList)
        {
            if (string.IsNullOrWhiteSpace(workflow.WorkflowId))
                throw new ArgumentException("Handoff workflow_id is required.", nameof(registry));
        }

        _workflows = workflowList.ToDictionary(static workflow => workflow.WorkflowId, static workflow => workflow, StringComparer.Ordinal);
        _defaultWorkflowId = string.IsNullOrWhiteSpace(registry.DefaultWorkflowId) ? workflowList[0].WorkflowId : registry.DefaultWorkflowId.Trim();
        if (!_workflows.ContainsKey(_defaultWorkflowId))
            throw new ArgumentException($"Default handoff workflow '{_defaultWorkflowId}' is not configured.", nameof(registry));
    }

    public string Name => "handoff";

    public string Description => "Manage session-scoped workflow handoff items. Supports list, upsert, patch, transition, and remove via an action parameter.";

    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "action":{"type":"string","enum":["list","upsert","patch","transition","remove"],"default":"list"},
        "workflow_id":{"type":"string"},
        "handoff_id":{"type":"string"},
        "title":{"type":"string"},
        "kind":{"type":"string"},
        "stage":{"type":"string"},
        "target_skill":{"type":"string"},
        "intent":{"type":"string"},
        "category":{"type":"string"},
        "payload":{"type":"object"},
        "source":{"type":"string"},
        "acceptance":{"type":"string"},
        "status":{"type":"string"},
        "fingerprint":{"type":"string"},
        "related_todos":{"type":"array","items":{"type":"string"}},
        "related_files":{"type":"array","items":{"type":"string"}},
        "patch":{"type":"object"},
        "expected_revision":{"type":"integer"},
        "dispatch_id":{"type":"string"},
        "callback_summary":{"type":"string"},
        "reason":{"type":"string"}
      },
      "required":["action"]
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: handoff requires execution context.");

    public ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = document.RootElement;
        var action = GetString(root, "action") ?? "list";
        if (!TryResolveWorkflow(root, out var workflow, out var workflowError))
            return ValueTask.FromResult(workflowError);

        var sessionId = context.Session.Id;
        var result = action switch
        {
            "list" => List(sessionId, workflow, root),
            "upsert" => Upsert(sessionId, workflow, root),
            "patch" => Patch(sessionId, workflow, root),
            "transition" => Transition(sessionId, workflow, root),
            "remove" => Remove(sessionId, workflow, root),
            _ => "Error: Unknown action. Valid actions are list, upsert, patch, transition, and remove."
        };

        return ValueTask.FromResult(result);
    }

    private string List(string sessionId, HandoffWorkflowOptions workflow, JsonElement root)
    {
        var metadata = _metadataStore.Get(sessionId);
        var filteredItems = metadata.HandoffItems
            .Where(item => IsWorkflowItem(item, workflow))
            .Where(item => MatchesFilter(item, root, "kind", item.Kind))
            .Where(item => MatchesFilter(item, root, "stage", item.Stage))
            .Where(item => MatchesFilter(item, root, "target_skill", item.TargetSkill))
            .Where(item => MatchesFilter(item, root, "status", item.Status))
            .Where(item => MatchesFilter(item, root, "fingerprint", item.Fingerprint))
            .ToArray();

        return Serialize(new SessionHandoffListResponse
        {
            SessionId = sessionId,
            Items = filteredItems
        });
    }

    private string Upsert(string sessionId, HandoffWorkflowOptions workflow, JsonElement root)
    {
        var fingerprint = GetString(root, "fingerprint");
        if (string.IsNullOrWhiteSpace(fingerprint))
            return "Error: fingerprint is required.";

        var payloadError = TryReadObject(root, "payload", out var payload, out var payloadFound);
        if (payloadError is not null)
            return payloadError;

        var metadata = _metadataStore.Get(sessionId);
        var items = metadata.HandoffItems.ToList();
        var existingIndex = items.FindIndex(item => IsWorkflowItem(item, workflow) && string.Equals(item.Fingerprint, fingerprint, StringComparison.Ordinal));
        var now = DateTimeOffset.UtcNow;

        if (existingIndex >= 0)
        {
            var existing = items[existingIndex];
            var kind = GetString(root, "kind") ?? existing.Kind;
            var stage = GetString(root, "stage") ?? existing.Stage;
            var targetSkill = GetString(root, "target_skill") ?? existing.TargetSkill;
            var status = GetString(root, "status") ?? existing.Status;

            var validationError = ValidateHandoffShape(workflow, kind, stage, targetSkill, status);
            if (validationError is not null)
                return validationError;
            if (!workflow.CanTransition(existing.Status, status))
                return InvalidTransition(existing.HandoffId, existing.Status, status);

            items[existingIndex] = new SessionHandoffItem
            {
                SessionId = sessionId,
                WorkflowId = existing.WorkflowId,
                HandoffId = existing.HandoffId,
                Title = GetString(root, "title") ?? existing.Title,
                Kind = kind,
                Stage = stage,
                TargetSkill = targetSkill,
                Intent = GetString(root, "intent") ?? existing.Intent,
                Category = GetString(root, "category") ?? existing.Category,
                Payload = payloadFound ? MergePayload(existing.Payload, payload) : ClonePayload(existing.Payload),
                Source = GetString(root, "source") ?? existing.Source,
                Acceptance = GetString(root, "acceptance") ?? existing.Acceptance,
                Status = status,
                Fingerprint = existing.Fingerprint,
                RelatedTodos = GetStringArray(root, "related_todos", existing.RelatedTodos),
                RelatedFiles = GetStringArray(root, "related_files", existing.RelatedFiles),
                Revision = existing.Revision + 1,
                CreatedAtUtc = existing.CreatedAtUtc,
                UpdatedAtUtc = now,
                DispatchId = GetString(root, "dispatch_id") ?? existing.DispatchId,
                CallbackSummary = GetString(root, "callback_summary") ?? existing.CallbackSummary
            };

            return SaveMutation(sessionId, workflow.WorkflowId, items, existing.HandoffId);
        }

        var title = GetString(root, "title");
        if (string.IsNullOrWhiteSpace(title))
            return "Error: title is required.";
        var stageForNewItem = GetString(root, "stage");
        if (string.IsNullOrWhiteSpace(stageForNewItem))
            return "Error: stage is required.";
        var targetSkillForNewItem = GetString(root, "target_skill");
        if (string.IsNullOrWhiteSpace(targetSkillForNewItem))
            return "Error: target_skill is required.";
        if (!payloadFound)
            return "Error: payload is required.";

        var kindForNewItem = GetString(root, "kind") ?? workflow.Kind;
        var statusForNewItem = GetString(root, "status") ?? workflow.DefaultStatus;
        var newItemValidationError = ValidateHandoffShape(workflow, kindForNewItem, stageForNewItem, targetSkillForNewItem, statusForNewItem);
        if (newItemValidationError is not null)
            return newItemValidationError;
        if (!workflow.IsValidNewItemStatus(statusForNewItem))
            return $"Error: new handoff status must be one of: {string.Join(", ", workflow.NewItemStatuses.Select(static status => $"'{status}'"))}.";

        var handoffId = CreateHandoffId(workflow, stageForNewItem);
        items.Add(new SessionHandoffItem
        {
            SessionId = sessionId,
            WorkflowId = workflow.WorkflowId,
            HandoffId = handoffId,
            Title = title,
            Kind = kindForNewItem,
            Stage = stageForNewItem,
            TargetSkill = targetSkillForNewItem,
            Intent = GetString(root, "intent"),
            Category = GetString(root, "category"),
            Payload = payload,
            Source = GetString(root, "source"),
            Acceptance = GetString(root, "acceptance"),
            Status = statusForNewItem,
            Fingerprint = fingerprint,
            RelatedTodos = GetStringArray(root, "related_todos", []),
            RelatedFiles = GetStringArray(root, "related_files", []),
            Revision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            DispatchId = GetString(root, "dispatch_id"),
            CallbackSummary = GetString(root, "callback_summary")
        });

        return SaveMutation(sessionId, workflow.WorkflowId, items, handoffId);
    }

    private string Patch(string sessionId, HandoffWorkflowOptions workflow, JsonElement root)
    {
        var handoffId = GetString(root, "handoff_id");
        if (string.IsNullOrWhiteSpace(handoffId))
            return "Error: handoff_id is required.";

        var revisionError = TryReadExpectedRevision(root, out var expectedRevision);
        if (revisionError is not null)
            return revisionError;

        var patchError = TryReadObject(root, "patch", out var patch, out var patchFound);
        if (patchError is not null)
            return patchError;
        if (!patchFound)
            return "Error: patch is required.";

        var metadata = _metadataStore.Get(sessionId);
        var items = metadata.HandoffItems.ToList();
        var existingIndex = FindHandoffIndex(items, workflow, handoffId);
        if (existingIndex < 0)
            return $"Error: handoff '{handoffId}' was not found.";

        var existing = items[existingIndex];
        if (expectedRevision.HasValue && expectedRevision.Value != existing.Revision)
            return RevisionMismatch(handoffId, existing.Revision, expectedRevision.Value);

        var kind = GetString(patch, "kind") ?? existing.Kind;
        var stage = GetString(patch, "stage") ?? existing.Stage;
        var targetSkill = GetString(patch, "target_skill") ?? existing.TargetSkill;
        var status = GetString(patch, "status") ?? existing.Status;
        var fingerprint = GetString(patch, "fingerprint") ?? existing.Fingerprint;

        var validationError = ValidateHandoffShape(workflow, kind, stage, targetSkill, status);
        if (validationError is not null)
            return validationError;
        if (string.IsNullOrWhiteSpace(fingerprint))
            return "Error: fingerprint is required.";
        if (items.Where((item, index) => index != existingIndex).Any(item => IsWorkflowItem(item, workflow) && string.Equals(item.Fingerprint, fingerprint, StringComparison.Ordinal)))
            return $"Error: fingerprint '{fingerprint}' already belongs to another handoff item in workflow '{workflow.WorkflowId}' for this session.";
        if (!workflow.CanTransition(existing.Status, status))
            return InvalidTransition(existing.HandoffId, existing.Status, status);

        var patchPayloadError = TryReadObject(patch, "payload", out var payloadPatch, out var payloadPatchFound);
        if (patchPayloadError is not null)
            return patchPayloadError;

        items[existingIndex] = new SessionHandoffItem
        {
            SessionId = sessionId,
            WorkflowId = existing.WorkflowId,
            HandoffId = existing.HandoffId,
            Title = GetString(patch, "title") ?? existing.Title,
            Kind = kind,
            Stage = stage,
            TargetSkill = targetSkill,
            Intent = GetString(patch, "intent") ?? existing.Intent,
            Category = GetString(patch, "category") ?? existing.Category,
            Payload = payloadPatchFound ? MergePayload(existing.Payload, payloadPatch) : ClonePayload(existing.Payload),
            Source = GetString(patch, "source") ?? existing.Source,
            Acceptance = GetString(patch, "acceptance") ?? existing.Acceptance,
            Status = status,
            Fingerprint = fingerprint,
            RelatedTodos = GetStringArray(patch, "related_todos", existing.RelatedTodos),
            RelatedFiles = GetStringArray(patch, "related_files", existing.RelatedFiles),
            Revision = existing.Revision + 1,
            CreatedAtUtc = existing.CreatedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            DispatchId = GetString(patch, "dispatch_id") ?? existing.DispatchId,
            CallbackSummary = GetString(patch, "callback_summary") ?? existing.CallbackSummary
        };

        return SaveMutation(sessionId, workflow.WorkflowId, items, existing.HandoffId);
    }

    private string Transition(string sessionId, HandoffWorkflowOptions workflow, JsonElement root)
    {
        var handoffId = GetString(root, "handoff_id");
        if (string.IsNullOrWhiteSpace(handoffId))
            return "Error: handoff_id is required.";
        var status = GetString(root, "status");
        if (string.IsNullOrWhiteSpace(status))
            return "Error: status is required.";
        if (!workflow.IsValidStatus(status))
            return $"Error: status '{status}' is not valid for workflow '{workflow.WorkflowId}'.";

        var revisionError = TryReadExpectedRevision(root, out var expectedRevision);
        if (revisionError is not null)
            return revisionError;

        var metadata = _metadataStore.Get(sessionId);
        var items = metadata.HandoffItems.ToList();
        var existingIndex = FindHandoffIndex(items, workflow, handoffId);
        if (existingIndex < 0)
            return $"Error: handoff '{handoffId}' was not found.";

        var existing = items[existingIndex];
        if (expectedRevision.HasValue && expectedRevision.Value != existing.Revision)
            return RevisionMismatch(handoffId, existing.Revision, expectedRevision.Value);
        if (!workflow.CanTransition(existing.Status, status))
            return InvalidTransition(existing.HandoffId, existing.Status, status);

        items[existingIndex] = new SessionHandoffItem
        {
            SessionId = sessionId,
            WorkflowId = existing.WorkflowId,
            HandoffId = existing.HandoffId,
            Title = existing.Title,
            Kind = existing.Kind,
            Stage = existing.Stage,
            TargetSkill = existing.TargetSkill,
            Intent = existing.Intent,
            Category = existing.Category,
            Payload = ClonePayload(existing.Payload),
            Source = existing.Source,
            Acceptance = existing.Acceptance,
            Status = status,
            Fingerprint = existing.Fingerprint,
            RelatedTodos = existing.RelatedTodos,
            RelatedFiles = existing.RelatedFiles,
            Revision = existing.Revision + 1,
            CreatedAtUtc = existing.CreatedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            DispatchId = GetString(root, "dispatch_id") ?? existing.DispatchId,
            CallbackSummary = GetString(root, "callback_summary") ?? existing.CallbackSummary
        };

        return SaveMutation(sessionId, workflow.WorkflowId, items, existing.HandoffId);
    }

    private string Remove(string sessionId, HandoffWorkflowOptions workflow, JsonElement root)
    {
        var handoffId = GetString(root, "handoff_id");
        if (string.IsNullOrWhiteSpace(handoffId))
            return "Error: handoff_id is required.";
        var reason = GetString(root, "reason");
        if (string.IsNullOrWhiteSpace(reason))
            return "Error: reason is required.";

        var metadata = _metadataStore.Get(sessionId);
        var items = metadata.HandoffItems.ToList();
        var removedCount = items.RemoveAll(item => IsWorkflowItem(item, workflow) && string.Equals(item.HandoffId, handoffId, StringComparison.Ordinal));
        if (removedCount == 0)
            return $"Error: handoff '{handoffId}' was not found.";

        var updatedMetadata = _metadataStore.Set(sessionId, new SessionMetadataUpdateRequest
        {
            HandoffItems = items
        });

        return Serialize(new SessionHandoffRemoveResponse
        {
            SessionId = sessionId,
            HandoffId = handoffId,
            Removed = true,
            Reason = reason,
            Items = FilterWorkflowItems(updatedMetadata.HandoffItems, workflow.WorkflowId)
        });
    }

    private string SaveMutation(string sessionId, string workflowId, IReadOnlyList<SessionHandoffItem> items, string handoffId)
    {
        var updatedMetadata = _metadataStore.Set(sessionId, new SessionMetadataUpdateRequest
        {
            HandoffItems = items
        });
        var workflowItems = FilterWorkflowItems(updatedMetadata.HandoffItems, workflowId);
        var updatedItem = workflowItems.FirstOrDefault(item => string.Equals(item.HandoffId, handoffId, StringComparison.Ordinal));
        if (updatedItem is null)
            return $"Error: handoff '{handoffId}' was not found after update.";

        return Serialize(new SessionHandoffMutationResponse
        {
            SessionId = sessionId,
            Item = updatedItem,
            Items = workflowItems
        });
    }

    private bool TryResolveWorkflow(JsonElement root, out HandoffWorkflowOptions workflow, out string workflowError)
    {
        var workflowId = GetString(root, "workflow_id") ?? _defaultWorkflowId;
        if (_workflows.TryGetValue(workflowId, out workflow!))
        {
            workflowError = "";
            return true;
        }

        workflowError = $"Error: workflow_id '{workflowId}' is not registered.";
        return false;
    }

    private static bool IsWorkflowItem(SessionHandoffItem item, HandoffWorkflowOptions workflow)
        => string.Equals(item.WorkflowId, workflow.WorkflowId, StringComparison.Ordinal);

    private static int FindHandoffIndex(IReadOnlyList<SessionHandoffItem> items, HandoffWorkflowOptions workflow, string handoffId)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (IsWorkflowItem(items[index], workflow) && string.Equals(items[index].HandoffId, handoffId, StringComparison.Ordinal))
                return index;
        }

        return -1;
    }

    private static SessionHandoffItem[] FilterWorkflowItems(IReadOnlyList<SessionHandoffItem> items, string workflowId)
        => items.Where(item => string.Equals(item.WorkflowId, workflowId, StringComparison.Ordinal)).ToArray();

    private static bool MatchesFilter(SessionHandoffItem item, JsonElement root, string propertyName, string value)
    {
        var filter = GetString(root, propertyName);
        return string.IsNullOrWhiteSpace(filter) || string.Equals(value, filter, StringComparison.Ordinal);
    }

    private static string? ValidateHandoffShape(HandoffWorkflowOptions workflow, string kind, string stage, string targetSkill, string status)
    {
        if (!string.Equals(kind, workflow.Kind, StringComparison.Ordinal))
            return $"Error: kind must be '{workflow.Kind}' for workflow '{workflow.WorkflowId}'.";
        if (!workflow.IsValidStage(stage))
            return $"Error: stage '{stage}' is not valid for workflow '{workflow.WorkflowId}'.";
        if (!workflow.IsValidTargetSkill(targetSkill))
            return $"Error: target_skill '{targetSkill}' is not valid for workflow '{workflow.WorkflowId}'.";
        if (!workflow.IsValidStatus(status))
            return $"Error: status '{status}' is not valid for workflow '{workflow.WorkflowId}'.";
        return null;
    }

    private static string InvalidTransition(string handoffId, string currentStatus, string nextStatus)
        => $"Error: handoff '{handoffId}' cannot transition from '{currentStatus}' to '{nextStatus}'.";

    private static string RevisionMismatch(string handoffId, int currentRevision, int expectedRevision)
        => $"Error: expected_revision mismatch for handoff '{handoffId}'. Current revision is {currentRevision}, but expected {expectedRevision}.";

    private static string? TryReadExpectedRevision(JsonElement root, out int? expectedRevision)
    {
        expectedRevision = null;
        if (!root.TryGetProperty("expected_revision", out var element))
            return null;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
            return "Error: expected_revision must be an integer.";
        expectedRevision = value;
        return null;
    }

    private static string? TryReadObject(JsonElement root, string propertyName, out JsonElement value, out bool found)
    {
        value = default;
        found = false;
        if (!root.TryGetProperty(propertyName, out var element))
            return null;
        found = true;
        if (element.ValueKind != JsonValueKind.Object)
            return $"Error: {propertyName} must be an object.";
        value = element.Clone();
        return null;
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
            return null;
        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string[] GetStringArray(JsonElement root, string propertyName, string[] fallback)
    {
        if (!root.TryGetProperty(propertyName, out var element))
            return fallback;
        if (element.ValueKind != JsonValueKind.Array)
            return fallback;

        return element.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static JsonElement MergePayload(JsonElement existing, JsonElement incoming)
    {
        if (existing.ValueKind != JsonValueKind.Object || incoming.ValueKind != JsonValueKind.Object)
            return incoming.Clone();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteMergedObject(writer, existing, incoming);
        }

        stream.Position = 0;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static void WriteMergedObject(Utf8JsonWriter writer, JsonElement existing, JsonElement incoming)
    {
        writer.WriteStartObject();
        var handledPatchNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var existingProperty in existing.EnumerateObject())
        {
            if (incoming.TryGetProperty(existingProperty.Name, out var incomingValue))
            {
                handledPatchNames.Add(existingProperty.Name);
                writer.WritePropertyName(existingProperty.Name);
                if (existingProperty.Value.ValueKind == JsonValueKind.Object && incomingValue.ValueKind == JsonValueKind.Object)
                    WriteMergedObject(writer, existingProperty.Value, incomingValue);
                else
                    incomingValue.WriteTo(writer);
            }
            else
            {
                existingProperty.WriteTo(writer);
            }
        }

        foreach (var incomingProperty in incoming.EnumerateObject())
        {
            if (!handledPatchNames.Contains(incomingProperty.Name))
                incomingProperty.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    private static JsonElement ClonePayload(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Undefined)
            return EmptyObject();
        return payload.Clone();
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static string CreateHandoffId(HandoffWorkflowOptions workflow, string stage)
    {
        var prefix = workflow.GetIdPrefix(stage);
        return $"{prefix}_{Guid.NewGuid():N}"[..18];
    }

    private static string Serialize(SessionHandoffListResponse response)
        => JsonSerializer.Serialize(response, CoreJsonContext.Default.SessionHandoffListResponse);

    private static string Serialize(SessionHandoffMutationResponse response)
        => JsonSerializer.Serialize(response, CoreJsonContext.Default.SessionHandoffMutationResponse);

    private static string Serialize(SessionHandoffRemoveResponse response)
        => JsonSerializer.Serialize(response, CoreJsonContext.Default.SessionHandoffRemoveResponse);
}
