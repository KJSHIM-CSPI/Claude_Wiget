using System.Globalization;
using System.Text.Json;

namespace ClaudeUsageWidget.Core;

public sealed record UsageBucket(double Percent, DateTimeOffset? ResetsAt);
public sealed record UsageSnapshot(UsageBucket? FiveHour, UsageBucket? Fable, DateTimeOffset RetrievedAt);

public static class UsageParser
{
    // The claude.ai web endpoint expresses utilization in percent, not a 0..1 fraction.
    public static UsageSnapshot Parse(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("사용량 응답 형식이 올바르지 않습니다.");
        var five = root.TryGetProperty("five_hour", out var value) ? ReadBucket(value) : null;
        UsageBucket? fable = null;
        foreach (var item in root.EnumerateObject())
        {
            if (item.Name.Contains("fable", StringComparison.OrdinalIgnoreCase))
                fable ??= ReadBucket(item.Value);
        }
        foreach (var arrayName in new[] { "model_scoped", "limits" })
        {
            if (!root.TryGetProperty(arrayName, out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("is_active", out var active) && active.ValueKind == JsonValueKind.False) continue;
                if (item.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "session")
                    five ??= ReadBucket(item);
                var isFable = new[] { "display_name", "model", "model_name", "name", "id" }
                    .Any(key => item.TryGetProperty(key, out var label) && label.ValueKind == JsonValueKind.String &&
                        label.GetString()!.Contains("fable", StringComparison.OrdinalIgnoreCase));
                if (item.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object &&
                    scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object &&
                    model.TryGetProperty("display_name", out var name) && name.ValueKind == JsonValueKind.String)
                    isFable |= name.GetString()!.Contains("fable", StringComparison.OrdinalIgnoreCase);
                if (isFable) fable ??= ReadBucket(item);
            }
        }
        if (five is null && fable is null)
            throw new FormatException("사용량 항목을 찾지 못했습니다. 로그인과 Claude 사용량 화면을 확인해 주세요.");
        return new(five, fable, now);
    }

    private static UsageBucket? ReadBucket(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (!item.TryGetProperty("utilization", out var percentage) &&
            !item.TryGetProperty("used_percentage", out percentage) && !item.TryGetProperty("percent", out percentage)) return null;
        if (percentage.ValueKind != JsonValueKind.Number || !percentage.TryGetDouble(out var number) ||
            !double.IsFinite(number) || number < 0 || number > 100) return null;
        DateTimeOffset? resets = null;
        if (item.TryGetProperty("resets_at", out var reset) && reset.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed)) resets = parsed;
        return new(number, resets);
    }
}

public static class UsageClock
{
    public static string Remaining(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is null) return "시간 정보 없음";
        var remaining = reset.Value - now;
        if (remaining <= TimeSpan.Zero) return "초기화 확인 대기";
        var seconds = (long)Math.Ceiling(remaining.TotalSeconds);
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    }

    public static TimeSpan RetryDelay(int intervalSeconds, int failures, int? retryAfterSeconds = null)
    {
        var backoff = Math.Min(900, Math.Max(intervalSeconds, 30) * Math.Pow(2, Math.Clamp(failures - 1, 0, 5)));
        return TimeSpan.FromSeconds(Math.Max(intervalSeconds, Math.Max(backoff, Math.Clamp(retryAfterSeconds ?? 0, 0, 86400))));
    }
}
