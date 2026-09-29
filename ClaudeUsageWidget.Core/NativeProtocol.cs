using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeUsageWidget.Core;

public static class NativeProtocol
{
    public const string ExtensionId = "lnfieklpgjnhhonoebelmhkmccnifafc";
    public const string Origin = "chrome-extension://" + ExtensionId + "/";
    public static string PipeName => "ClaudeUsageWidget.v3." + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..24];
    public const int MaxBytes = 32 * 1024;

    public static async Task<JsonElement> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxBytes) throw new IOException("Invalid native message size.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, token);
        using var document = JsonDocument.Parse(data);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("Invalid native message.");
        return document.RootElement.Clone();
    }

    public static async Task WriteAsync(Stream stream, object message, CancellationToken token)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message);
        if (data.Length > MaxBytes) throw new IOException("Native message too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(data, token);
        await stream.FlushAsync(token);
    }
}
