using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeUsageWidget.Core;

public sealed class CdpProtocolException(string method, int code) : IOException($"브라우저 명령을 처리하지 못했습니다 ({method}, {code}).");

public sealed record BrowserEndpoint(int Port, string BrowserPath)
{
    public Uri HttpBase => new($"http://127.0.0.1:{Port}/");
    public Uri BrowserSocket => new($"ws://127.0.0.1:{Port}{BrowserPath}");

    public static BrowserEndpoint Parse(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 2 || !int.TryParse(lines[0], out var port) || port < 1 || port > 65535 ||
            !Regex.IsMatch(lines[1], @"^/devtools/browser/[A-Za-z0-9-]+$"))
            throw new FormatException("브라우저 연결 정보를 읽지 못했습니다.");
        return new(port, lines[1]);
    }

    public Uri ValidateSocket(string address, bool browser = false)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "ws" ||
            uri.Host != "127.0.0.1" || uri.Port != Port || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (browser ? uri.AbsolutePath != BrowserPath : !Regex.IsMatch(uri.AbsolutePath, @"^/devtools/page/[A-Za-z0-9-]+$")))
            throw new FormatException("위젯 전용 브라우저의 로컬 연결이 아닙니다.");
        return uri;
    }
}

public static class CdpConnection
{
    // A fresh connection per command avoids stale callbacks, overlapping socket reads and cached page contexts.
    public static async Task<JsonElement> CommandAsync(Uri socketAddress, string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (socketAddress.Scheme != "ws" || socketAddress.Host != "127.0.0.1")
            throw new ArgumentException("Only literal loopback browser connections are allowed.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        await socket.ConnectAsync(socketAddress, timeout.Token);
        var command = JsonSerializer.SerializeToUtf8Bytes(new { id = 1, method, @params = parameters ?? new { } });
        await socket.SendAsync(command.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[8192];
        while (true)
        {
            using var content = new MemoryStream();
            ValueWebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                if (received.MessageType == WebSocketMessageType.Close) throw new IOException("브라우저 연결이 종료되었습니다.");
                if (received.MessageType != WebSocketMessageType.Text) throw new IOException("브라우저 응답 형식이 올바르지 않습니다.");
                if (content.Length + received.Count > 2 * 1024 * 1024) throw new IOException("브라우저 응답이 너무 큽니다.");
                content.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);
            using var document = JsonDocument.Parse(content.ToArray());
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var number) || number != 1) continue;
            if (root.TryGetProperty("error", out var error))
                throw new CdpProtocolException(method, error.TryGetProperty("code", out var code) && code.TryGetInt32(out var errorCode) ? errorCode : 0);
            if (!root.TryGetProperty("result", out var result)) throw new IOException("브라우저 응답을 읽지 못했습니다.");
            return result.Clone();
        }
    }
}
