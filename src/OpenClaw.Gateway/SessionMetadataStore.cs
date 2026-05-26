using System.Text.Json;
using OpenClaw.Core.Abstractions;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class SessionMetadataStore : ISessionMetadataStore
{
    private const string DirectoryName = "admin";
    private const string FileName = "session-metadata.json";
    private const string LegacyDefaultHandoffWorkflowId = "employment-coach";

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly ILogger<SessionMetadataStore> _logger;
    private List<SessionMetadataSnapshot>? _cached;

    public SessionMetadataStore(string storagePath, ILogger<SessionMetadataStore> logger)
    {
        var rootedStoragePath = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        _path = Path.Combine(rootedStoragePath, DirectoryName, FileName);
        _logger = logger;
    }

    public SessionMetadataSnapshot Get(string sessionId)
    {
        lock (_gate)
        {
            return LoadUnsafe().FirstOrDefault(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
                ?? new SessionMetadataSnapshot
                {
                    SessionId = sessionId,
                    Starred = false,
                    Tags = [],
                    TodoItems = [],
                    HandoffItems = []
                };
        }
    }

    public IReadOnlyDictionary<string, SessionMetadataSnapshot> GetAll()
    {
        lock (_gate)
        {
            return LoadUnsafe().ToDictionary(static item => item.SessionId, StringComparer.Ordinal);
        }
    }

    public SessionMetadataSnapshot Set(string sessionId, SessionMetadataUpdateRequest request)
    {
        lock (_gate)
        {
            var items = LoadUnsafe();
            var current = items.FirstOrDefault(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
                ?? new SessionMetadataSnapshot
                {
                    SessionId = sessionId,
                    Starred = false,
                    Tags = [],
                    TodoItems = [],
                    HandoffItems = []
                };

            var updated = new SessionMetadataSnapshot
            {
                SessionId = sessionId,
                Starred = request.Starred ?? current.Starred,
                Tags = (request.Tags ?? current.Tags)
                    .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(static tag => tag.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static tag => tag, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                ActivePresetId = string.IsNullOrWhiteSpace(request.ActivePresetId)
                    ? current.ActivePresetId
                    : request.ActivePresetId.Trim(),
                TodoItems = NormalizeTodoItems(request.TodoItems ?? current.TodoItems),
                HandoffItems = NormalizeHandoffItems(sessionId, request.HandoffItems ?? current.HandoffItems)
            };

            items.RemoveAll(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal));
            items.Add(updated);
            SaveUnsafe(items);
            return updated;
        }
    }

    private List<SessionMetadataSnapshot> LoadUnsafe()
    {
        if (_cached is not null)
            return _cached;

        if (AtomicJsonFileStore.TryLoad(_path, CoreJsonContext.Default.ListSessionMetadataSnapshot, out List<SessionMetadataSnapshot>? items, out var error))
        {
            _cached = items ?? [];
            return _cached;
        }

        _logger.LogWarning("Failed to load session metadata from {Path}: {Error}", _path, error);
        _cached = [];

        return _cached;
    }

    private void SaveUnsafe(List<SessionMetadataSnapshot> items)
    {
        if (!AtomicJsonFileStore.TryWriteAtomic(_path, items, CoreJsonContext.Default.ListSessionMetadataSnapshot, out var error))
        {
            _logger.LogWarning("Failed to save session metadata to {Path}: {Error}", _path, error);
            throw new InvalidOperationException($"Failed to persist session metadata: {error}");
        }

        _cached = items;
    }

    private static IReadOnlyList<SessionTodoItem> NormalizeTodoItems(IReadOnlyList<SessionTodoItem>? items)
    {
        if (items is null || items.Count == 0)
            return [];

        return items
            .Where(static item => !string.IsNullOrWhiteSpace(item.Text))
            .Select(static item => new SessionTodoItem
            {
                Id = string.IsNullOrWhiteSpace(item.Id) ? $"todo_{Guid.NewGuid():N}"[..17] : item.Id.Trim(),
                Text = item.Text.Trim(),
                Completed = item.Completed,
                Notes = string.IsNullOrWhiteSpace(item.Notes) ? null : item.Notes.Trim(),
                CreatedAtUtc = item.CreatedAtUtc == default ? DateTimeOffset.UtcNow : item.CreatedAtUtc,
                UpdatedAtUtc = item.UpdatedAtUtc == default ? DateTimeOffset.UtcNow : item.UpdatedAtUtc
            })
            .OrderBy(static item => item.Completed)
            .ThenBy(static item => item.CreatedAtUtc)
            .ToArray();
    }

    private static IReadOnlyList<SessionHandoffItem> NormalizeHandoffItems(string sessionId, IReadOnlyList<SessionHandoffItem>? items)
    {
        if (items is null || items.Count == 0)
            return [];

        return items
            .Where(static item => !string.IsNullOrWhiteSpace(item.HandoffId) || !string.IsNullOrWhiteSpace(item.Fingerprint))
            .Select(item => new SessionHandoffItem
            {
                SessionId = sessionId,
                WorkflowId = string.IsNullOrWhiteSpace(item.WorkflowId) ? LegacyDefaultHandoffWorkflowId : item.WorkflowId.Trim(),
                HandoffId = string.IsNullOrWhiteSpace(item.HandoffId) ? CreateHandoffId(item.Stage) : item.HandoffId.Trim(),
                Title = item.Title.Trim(),
                Kind = string.IsNullOrWhiteSpace(item.Kind) ? "handoff_todo" : item.Kind.Trim(),
                Stage = item.Stage.Trim(),
                TargetSkill = item.TargetSkill.Trim(),
                Intent = string.IsNullOrWhiteSpace(item.Intent) ? null : item.Intent.Trim(),
                Category = string.IsNullOrWhiteSpace(item.Category) ? null : item.Category.Trim(),
                Payload = item.Payload.ValueKind == JsonValueKind.Undefined ? EmptyObject() : item.Payload.Clone(),
                Source = string.IsNullOrWhiteSpace(item.Source) ? null : item.Source.Trim(),
                Acceptance = string.IsNullOrWhiteSpace(item.Acceptance) ? null : item.Acceptance.Trim(),
                Status = string.IsNullOrWhiteSpace(item.Status) ? "drafting" : item.Status.Trim(),
                Fingerprint = item.Fingerprint.Trim(),
                RelatedTodos = NormalizeStringArray(item.RelatedTodos),
                RelatedFiles = NormalizeStringArray(item.RelatedFiles),
                Revision = Math.Max(1, item.Revision),
                CreatedAtUtc = item.CreatedAtUtc == default ? DateTimeOffset.UtcNow : item.CreatedAtUtc,
                UpdatedAtUtc = item.UpdatedAtUtc == default ? DateTimeOffset.UtcNow : item.UpdatedAtUtc,
                DispatchId = string.IsNullOrWhiteSpace(item.DispatchId) ? null : item.DispatchId.Trim(),
                CallbackSummary = string.IsNullOrWhiteSpace(item.CallbackSummary) ? null : item.CallbackSummary.Trim()
            })
            .OrderBy(static item => item.CreatedAtUtc)
            .ToArray();
    }

    private static string[] NormalizeStringArray(string[]? values)
        => values is null || values.Length == 0
            ? []
            : values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    private static string CreateHandoffId(string stage)
    {
        var prefix = stage.Trim() switch
        {
            "material" => "m",
            "skill" => "s",
            "external" => "e",
            _ => "h"
        };
        return $"{prefix}_{Guid.NewGuid():N}"[..18];
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
