using ClaudeUsageWidget.Core;

if (args.Contains("--oauth-probe")) { await OAuthChecks.ProbeAsync(); return; }
await OAuthChecks.RunAsync();

var now = DateTimeOffset.Parse("2026-09-23T00:00:00Z");
var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
var usage = UsageParser.Parse("""{"five_hour":{"utilization":0.5,"resets_at":"2026-09-23T14:00:00+09:00"},"seven_day_fable":{"utilization":75,"resets_at":null}}""", now);
Check(usage.FiveHour?.Percent == 0.5, "percent is not mistakenly multiplied by 100");
Check(usage.Fable?.Percent == 75, "explicit Fable bucket");
Check(UsageClock.Remaining(usage.FiveHour?.ResetsAt, now) == "05:00:00", "reset timezone and countdown");
var model = UsageParser.Parse("""{"five_hour":{"utilization":10},"limits":[{"display_name":"Fable 5.1","utilization":27.2}]}""", now);
Check(model.Fable?.Percent == 27.2, "named model limits");
var scoped = UsageParser.Parse("""{"model_scoped":[{"display_name":"Fable","used_percentage":32}]}""", now);
Check(scoped.Fable?.Percent == 32, "named model_scoped limits");
var noFable = UsageParser.Parse("""{"five_hour":{"utilization":5},"seven_day":{"utilization":70},"seven_day_overage_included":{"utilization":90}}""", now);
Check(noFable.Fable is null, "unlabeled weekly or overage bucket is never attributed to Fable");
Check(UsageClock.Remaining(null, now) == "시간 정보 없음", "missing reset");
Check(UsageClock.Remaining(now.AddMilliseconds(100), now) == "00:00:01", "partial seconds round up");
Check(UsageClock.Remaining(now.AddSeconds(-1), now) == "초기화 확인 대기", "expired reset does not fabricate fresh usage");
Check(UsageClock.Remaining(now.AddDays(2).AddHours(1), now) == "49:00:00", "countdown preserves total hours");
Check(UsageClock.RetryDelay(30, 1).TotalSeconds == 30, "initial retry");
Check(UsageClock.RetryDelay(30, 8).TotalSeconds == 900, "bounded retry backoff");
Check(UsageClock.RetryDelay(30, 2, 1200).TotalSeconds == 1200, "server retry-after respected");
Check(UsageClock.RetryDelay(3600, 1).TotalSeconds == 3600, "retry never exceeds the user-requested frequency");
foreach (var bad in new[] { "{}", "[]", "{\"five_hour\":{\"utilization\":101}}", "{\"five_hour\":{\"utilization\":\"50\"}}" })
{
    var failed = false;
    try { UsageParser.Parse(bad, now); } catch (FormatException) { failed = true; }
    Check(failed, "reject malformed or unsupported payload: " + bad);
}
var settings = new WidgetSettings { RefreshSeconds = 0, Opacity = double.NaN, ManualFable = 101, ManualFiveHour = -1 };
settings.Normalize();
Check(settings.RefreshSeconds == 10 && settings.Opacity == 0.94 && settings.ManualFable == 100 && settings.ManualFiveHour == 0, "settings validation");
var temp = Path.Combine(Path.GetTempPath(), "claude-widget-test-" + Guid.NewGuid().ToString("N"), "settings.json");
try
{
    SettingsStore.Save(temp, new WidgetSettings { RefreshSeconds = 45, ManualMode = true, ManualReset = now, AutoConnect = true });
    var saved = SettingsStore.Load(temp);
    Check(saved.RefreshSeconds == 45 && saved.ManualMode && saved.ManualReset == now && saved.AutoConnect, "settings and connection preference round trip");
    saved.AutoConnect = false;
    SettingsStore.Save(temp, saved);
    Check(!SettingsStore.Load(temp).AutoConnect, "signed out preference survives restart");
    File.WriteAllText(temp, """{"BrowserKind":"Edge","RefreshSeconds":45,"ConnectionDisabled":true,"AutoConnect":false}""");
    var migrated = SettingsStore.Load(temp);
    Check(migrated.RefreshSeconds == 45 && migrated.ConnectionDisabled && !migrated.AutoConnect,
        "legacy browser preference is ignored without losing current settings");
    File.WriteAllText(temp, "{broken");
    Check(SettingsStore.Load(temp).RefreshSeconds == 30, "corrupt settings recover defaults");
}
finally { if (File.Exists(temp)) File.Delete(temp); Directory.Delete(Path.GetDirectoryName(temp)!); }
Console.WriteLine($"{passed} checks passed.");
