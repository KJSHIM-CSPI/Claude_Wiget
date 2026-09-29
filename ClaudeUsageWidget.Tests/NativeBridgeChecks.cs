using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using ClaudeUsageWidget.Core;

internal static class NativeBridgeChecks
{
    public static async Task Run(string? hostPath)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var stream = new MemoryStream();
        await NativeProtocol.WriteAsync(stream, new { type = "test", label = "한글" }, stop.Token);
        stream.Position = 0;
        if ((await NativeProtocol.ReadAsync(stream, stop.Token)).GetProperty("label").GetString() != "한글") throw new Exception("UTF8 frame failed");
        foreach (var size in new[] { 0, -1, NativeProtocol.MaxBytes + 1 })
        {
            using var invalid = new MemoryStream(BitConverter.GetBytes(size));
            try { await NativeProtocol.ReadAsync(invalid, stop.Token); throw new Exception("Invalid size accepted"); }
            catch (IOException) { }
        }
        Console.WriteLine("PASS native frame UTF8 round trip and zero/negative/oversized rejection");
        if (!OperatingSystem.IsWindows()) return;
        var name = "ClaudeWidgetTest." + Guid.NewGuid().ToString("N");
        using var bridge = new ExtensionBridge(name);
        using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(stop.Token);
            await NativeProtocol.WriteAsync(client, new { type = "hello", origin = NativeProtocol.Origin }, stop.Token);
            await Connected(bridge, stop.Token);
            var fetch = bridge.FetchAsync();
            var command = await NativeProtocol.ReadAsync(client, stop.Token);
            await NativeProtocol.WriteAsync(client, new { id = command.GetProperty("id").GetString(), ok = true,
                usage = new { five_hour = new { utilization = 42, resets_at = "2026-09-28T08:00:00Z" } } }, stop.Token);
            if (UsageParser.Parse(await fetch, DateTimeOffset.UtcNow).FiveHour?.Percent != 42) throw new Exception("Pipe response failed");
            fetch = bridge.FetchAsync();
            command = await NativeProtocol.ReadAsync(client, stop.Token);
            await NativeProtocol.WriteAsync(client, new { id = command.GetProperty("id").GetString(), ok = false,
                kind = "challenge", status = 403, retryAfter = (int?)null }, stop.Token);
            try { await fetch; throw new Exception("Challenge accepted"); }
            catch (UsageConnectionException error) when (error.StatusCode == 403 && error.Message.Contains("보안 확인")) { }
        }
        while (bridge.IsConnected) await Task.Delay(10, stop.Token);
        using (var reconnected = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await reconnected.ConnectAsync(stop.Token);
            await NativeProtocol.WriteAsync(reconnected, new { type = "hello", origin = NativeProtocol.Origin }, stop.Token);
            await Connected(bridge, stop.Token);
            var fetch = bridge.FetchAsync();
            await NativeProtocol.ReadAsync(reconnected, stop.Token);
            reconnected.Dispose();
            try { await fetch; throw new Exception("Disconnected request accepted"); } catch (UsageConnectionException) { }
        }
        Console.WriteLine("PASS real current-user named pipe usage, challenge/null retry, reconnect, mid-request disconnect");
        if (hostPath is null) return;
        using var nativeBridge = new ExtensionBridge();
        var start = new ProcessStartInfo(Path.GetFullPath(hostPath)) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add(NativeProtocol.Origin);
        using var host = Process.Start(start) ?? throw new Exception("Host did not start");
        try
        {
            await Connected(nativeBridge, stop.Token);
            var fetch = nativeBridge.FetchAsync();
            var command = await NativeProtocol.ReadAsync(host.StandardOutput.BaseStream, stop.Token);
            await NativeProtocol.WriteAsync(host.StandardInput.BaseStream, new { id = command.GetProperty("id").GetString(), ok = true,
                usage = new { five_hour = new { utilization = 27 } } }, stop.Token);
            if (UsageParser.Parse(await fetch, DateTimeOffset.UtcNow).FiveHour?.Percent != 27) throw new Exception("Native host failed");
            nativeBridge.Dispose();
            await host.WaitForExitAsync(stop.Token);
            if (host.ExitCode != 0) throw new Exception("Native host shutdown failed");
            Console.WriteLine("PASS published native host stdio-to-pipe round trip and shutdown");
        }
        finally { if (!host.HasExited) host.Kill(); }
    }

    private static async Task Connected(ExtensionBridge bridge, CancellationToken token)
    { while (!bridge.IsConnected) await Task.Delay(10, token); }
}
