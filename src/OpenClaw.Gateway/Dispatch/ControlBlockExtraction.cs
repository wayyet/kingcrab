using System.Text;
using System.Text.Json;

namespace OpenClaw.Gateway.Dispatch;

internal enum ControlBlockKind
{
    Dispatch,
    DispatchCallback
}

internal sealed record ControlBlock(ControlBlockKind Kind, string Json);

internal sealed record ControlBlockExtractionResult(string VisibleText, IReadOnlyList<ControlBlock> Blocks);

internal sealed record DispatchSignal(
    string Target,
    string[] HandoffIds,
    string? Mode,
    string? Note,
    string? To);

internal sealed record DispatchCallbackSignal(
    string SourceDispatchTarget,
    string[] HandoffIds,
    string UserSummary,
    DispatchTodoResult[] TodoResults,
    string Status,
    string[] Errors);

internal sealed record DispatchTodoResult(
    string HandoffId,
    string Status,
    string[] Artifacts,
    string[] Errors);

internal static class ControlBlockExtractor
{
    private const int MaxBlockChars = 64 * 1024;
    private const string DispatchStart = "<dispatch>";
    private const string DispatchEnd = "</dispatch>";
    private const string CallbackStart = "<dispatch_callback>";
    private const string CallbackEnd = "</dispatch_callback>";

    private static readonly string[] StartTags = [DispatchStart, CallbackStart];

    public static ControlBlockExtractionResult Extract(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new ControlBlockExtractionResult("", []);

        var filter = new StreamingControlBlockFilter();
        var visible = new StringBuilder();
        foreach (var chunk in filter.Append(text))
            visible.Append(chunk);
        foreach (var chunk in filter.Complete())
            visible.Append(chunk);

        return new ControlBlockExtractionResult(visible.ToString(), filter.Blocks);
    }

    public sealed class StreamingControlBlockFilter
    {
        private readonly StringBuilder _pending = new();
        private readonly List<ControlBlock> _blocks = [];
        private readonly StringBuilder _currentBlock = new();
        private ControlBlockKind? _insideKind;
        private string? _insideEndTag;

        public IReadOnlyList<ControlBlock> Blocks => _blocks;

        public IReadOnlyList<string> Append(string text)
        {
            if (!string.IsNullOrEmpty(text))
                _pending.Append(text);

            var visible = new List<string>();
            Process(visible, flush: false);
            return visible;
        }

        public IReadOnlyList<string> Complete()
        {
            var visible = new List<string>();
            Process(visible, flush: true);
            _pending.Clear();
            _currentBlock.Clear();
            _insideKind = null;
            _insideEndTag = null;
            return visible;
        }

        private void Process(List<string> visible, bool flush)
        {
            while (_pending.Length > 0)
            {
                if (_insideKind is null)
                {
                    var match = FindNextStartTag(_pending.ToString());
                    if (match.StartIndex >= 0)
                    {
                        if (match.StartIndex > 0)
                        {
                            EmitVisiblePrefix(visible, match.StartIndex);
                            continue;
                        }

                        _pending.Remove(0, match.StartTag.Length);
                        _insideKind = match.Kind;
                        _insideEndTag = match.EndTag;
                        _currentBlock.Clear();
                        continue;
                    }

                    var holdLength = flush ? 0 : LongestPossibleStartTagPrefix(_pending.ToString());
                    var emitLength = _pending.Length - holdLength;
                    if (emitLength <= 0)
                        break;

                    EmitVisiblePrefix(visible, emitLength);
                    continue;
                }

                var endTag = _insideEndTag!;
                var pendingText = _pending.ToString();
                var endIndex = pendingText.IndexOf(endTag, StringComparison.Ordinal);
                if (endIndex >= 0)
                {
                    _currentBlock.Append(pendingText.AsSpan(0, endIndex));
                    _pending.Remove(0, endIndex + endTag.Length);
                    if (_currentBlock.Length <= MaxBlockChars)
                        _blocks.Add(new ControlBlock(_insideKind.Value, _currentBlock.ToString()));

                    _currentBlock.Clear();
                    _insideKind = null;
                    _insideEndTag = null;
                    continue;
                }

                if (flush)
                {
                    _pending.Clear();
                    _currentBlock.Clear();
                    _insideKind = null;
                    _insideEndTag = null;
                    break;
                }

                if (_currentBlock.Length + _pending.Length > MaxBlockChars)
                {
                    _pending.Clear();
                    _currentBlock.Clear();
                    _insideKind = null;
                    _insideEndTag = null;
                    break;
                }

                _currentBlock.Append(pendingText);
                _pending.Clear();
                break;
            }
        }

        private void EmitVisiblePrefix(List<string> visible, int length)
        {
            var chunk = _pending.ToString(0, length);
            _pending.Remove(0, length);
            if (chunk.Length > 0)
                visible.Add(chunk);
        }
    }

    private static (int StartIndex, string StartTag, string EndTag, ControlBlockKind Kind) FindNextStartTag(string text)
    {
        var dispatchIndex = text.IndexOf(DispatchStart, StringComparison.Ordinal);
        var callbackIndex = text.IndexOf(CallbackStart, StringComparison.Ordinal);

        if (dispatchIndex < 0 && callbackIndex < 0)
            return (-1, "", "", ControlBlockKind.Dispatch);

        if (callbackIndex >= 0 && (dispatchIndex < 0 || callbackIndex < dispatchIndex))
            return (callbackIndex, CallbackStart, CallbackEnd, ControlBlockKind.DispatchCallback);

        return (dispatchIndex, DispatchStart, DispatchEnd, ControlBlockKind.Dispatch);
    }

    private static int LongestPossibleStartTagPrefix(string text)
    {
        var max = Math.Min(text.Length, StartTags.Max(static tag => tag.Length - 1));
        for (var length = max; length > 0; length--)
        {
            var suffix = text[^length..];
            if (StartTags.Any(tag => tag.StartsWith(suffix, StringComparison.Ordinal)))
                return length;
        }

        return 0;
    }
}

internal static class DispatchSignalParser
{
    public static bool TryParseDispatch(string json, out DispatchSignal signal, out string error)
    {
        signal = null!;
        error = "";

        if (!TryParseObject(json, out var root, out var document, out error))
            return false;

        using (document)
        {
            var target = GetString(root, "target");
            if (string.IsNullOrWhiteSpace(target))
            {
                error = "dispatch.target is required.";
                return false;
            }

            var handoffIds = GetStringArray(root, "handoff_ids");
            if (handoffIds.Length == 0)
            {
                var single = GetString(root, "handoff_id");
                if (!string.IsNullOrWhiteSpace(single))
                    handoffIds = [single.Trim()];
            }

            signal = new DispatchSignal(
                target.Trim(),
                handoffIds,
                TrimOrNull(GetString(root, "mode")),
                TrimOrNull(GetString(root, "note")),
                TrimOrNull(GetString(root, "to")));
            return true;
        }
    }

    public static bool TryParseCallback(string json, out DispatchCallbackSignal callback, out string error)
    {
        callback = null!;
        error = "";

        if (!TryParseObject(json, out var root, out var document, out error))
            return false;

        using (document)
        {
            var sourceTarget = GetString(root, "source_dispatch_target");
            if (string.IsNullOrWhiteSpace(sourceTarget))
            {
                error = "dispatch_callback.source_dispatch_target is required.";
                return false;
            }

            var handoffIds = GetStringArray(root, "handoff_ids");
            var userSummary = GetString(root, "user_summary");
            var status = GetString(root, "status");
            if (string.IsNullOrWhiteSpace(userSummary))
            {
                error = "dispatch_callback.user_summary is required.";
                return false;
            }

            var results = ReadTodoResults(root);
            callback = new DispatchCallbackSignal(
                sourceTarget.Trim(),
                handoffIds,
                userSummary.Trim(),
                results,
                string.IsNullOrWhiteSpace(status) ? "success" : status.Trim(),
                GetStringArray(root, "errors"));
            return true;
        }
    }

    private static bool TryParseObject(string json, out JsonElement root, out JsonDocument document, out string error)
    {
        root = default;
        document = null!;
        error = "";

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"Invalid control block JSON: {ex.Message}";
            return false;
        }

        root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "Control block JSON must be an object.";
            document.Dispose();
            return false;
        }

        return true;
    }

    private static DispatchTodoResult[] ReadTodoResults(JsonElement root)
    {
        if (!root.TryGetProperty("todo_results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<DispatchTodoResult>();
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var handoffId = GetString(item, "handoff_id");
            if (string.IsNullOrWhiteSpace(handoffId))
                continue;

            items.Add(new DispatchTodoResult(
                handoffId.Trim(),
                string.IsNullOrWhiteSpace(GetString(item, "status")) ? "success" : GetString(item, "status")!.Trim(),
                GetStringArray(item, "artifacts"),
                GetStringArray(item, "errors")));
        }

        return [.. items];
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string[] GetStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string? TrimOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
