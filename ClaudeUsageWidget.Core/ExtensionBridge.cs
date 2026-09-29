using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace ClaudeUsageWidget.Core;

// Accept only the current Windows user. No TCP listener or browser debugging port.
public sealed class ExtensionBridge : IDisposable
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writes = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private NamedPipeServerStream? _connection;
    private readonly Task _listener;
    public bool IsConnected => _connection?.IsConnected == true;

    public ExtensionBridge(string? pipeName = null)
    {
        _pipeName = pipeName ?? NativeProtocol.PipeName;
        _listener = ListenAsync();
    }

    private async Task ListenAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_lifetime.Token);
                var hello = await NativeProtocol.ReadAsync(pipe, _lifetime.Token).WaitAsync(TimeSpan.FromSeconds(5), _lifetime.Token);
                if (!hello.TryGetProperty("type", out var type) || type.GetString() != "hello" ||
                    !hello.TryGetProperty("origin", out var origin) || origin.GetString() != NativeProtocol.Origin)
                    throw new IOException("Unknown extension.");
                _connection = pipe;
                while (!_lifetime.IsCancellationRequested)
                {
                    var reply = await NativeProtocol.ReadAsync(pipe, _lifetime.Token);
                    if (reply.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                        _pending.TryRemove(id.GetString()!, out var completion)) completion.TrySetResult(reply);
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or JsonException or
                TimeoutException or InvalidOperationException or UnauthorizedAccessException) { }
            finally
            {
                _connection = null;
                pipe?.Dispose();
                foreach (var pair in _pending)
                    if (_pending.TryRemove(pair.Key, out var completion))
                        completion.TrySetException(new UsageConnectionException("Chrome 확장 연결이 끊겼습니다. 확장 프로그램에서 다시 연결해 주세요."));
            }
            if (!_lifetime.IsCancellationRequested)
                try { await Task.Delay(500, _lifetime.Token); } catch (OperationCanceledException) { }
        }
    }

    public async Task<string> FetchAsync()
    {
        var pipe = _connection;
        if (pipe is null || !pipe.IsConnected)
            throw new UsageConnectionException("확장 연결을 기다립니다. 기존 연결은 30초 안에 재연결됩니다. 처음이라면 ‘브라우저 연결’에서 설정해 주세요.");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            await _writes.WaitAsync(timeout.Token);
            try { await NativeProtocol.WriteAsync(pipe, new { type = "fetch", id }, timeout.Token); }
            finally { _writes.Release(); }
            var reply = await completion.Task.WaitAsync(timeout.Token);
            if (reply.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True && reply.TryGetProperty("usage", out var usage))
                return usage.GetRawText();
            var status = reply.TryGetProperty("status", out var statusValue) && statusValue.ValueKind == JsonValueKind.Number && statusValue.TryGetInt32(out var number) ? number : 0;
            int? retry = reply.TryGetProperty("retryAfter", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var seconds) ? seconds : null;
            var kind = reply.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : "";
            var message = kind switch
            {
                "no_tab" => "Chrome에서 Claude 탭을 열고 확장 프로그램의 ‘이 탭 연결’을 눌러 주세요.",
                "challenge" => "보안 확인으로 조회를 중지했습니다. Chrome에서 확인을 마친 뒤 확장 프로그램의 ‘이 탭 연결’을 눌러 주세요.",
                "paused" => "탭의 응답이 없어 조회를 중지했습니다. Chrome에서 탭을 확인하고 확장 프로그램의 ‘이 탭 연결’을 눌러 주세요.",
                "busy" => "이전 조회가 진행 중입니다. 잠시 후 다시 확인합니다.",
                "login" => "Chrome에서 Claude 로그인을 마친 뒤 확장 프로그램의 ‘이 탭 연결’을 눌러 주세요.",
                _ => ConnectionFailure.Describe(kind ?? "", "usage", status)
            };
            throw new UsageConnectionException(message, status, retry);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        { throw new UsageConnectionException("Chrome 탭의 응답을 기다리다 시간이 초과되었습니다. 탭 상태를 확인해 주세요."); }
        finally { _pending.TryRemove(id, out _); }
    }

    public void Dispose() { _lifetime.Cancel(); _connection?.Dispose(); }
}
