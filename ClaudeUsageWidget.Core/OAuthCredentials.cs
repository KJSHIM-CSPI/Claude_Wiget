using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeUsageWidget.Core;

// Deliberately not a record: generated ToString must never print credentials.
public sealed class OAuthCredentials
{
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsExpired => ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow.AddMinutes(1);
}

public static class ClaudeCodeCredentials
{
    public static string DefaultPath => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } directory
            ? directory : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        ".credentials.json");

    public static OAuthCredentials? Read(string? path = null)
    {
        try { return Parse(File.ReadAllText(path ?? DefaultPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    public static OAuthCredentials? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object ||
            !oauth.TryGetProperty("accessToken", out var token) || token.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(token.GetString())) return null;
        if (oauth.TryGetProperty("scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array &&
            !scopes.EnumerateArray().Any(s => s.ValueKind == JsonValueKind.String && s.GetString() == "user:profile")) return null;
        DateTimeOffset? expiry = null;
        if (oauth.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.Number && expires.TryGetInt64(out var milliseconds))
        {
            try { expiry = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        // Never copy or rotate Claude Code's refresh token.
        return new() { AccessToken = token.GetString()!, ExpiresAt = expiry };
    }
}

public interface IOAuthTokenStore
{
    OAuthCredentials? Load();
    void Save(OAuthCredentials credentials);
    void Delete();
}

public sealed class WindowsOAuthTokenStore(string path) : IOAuthTokenStore
{
    public void Delete()
    {
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { } // A fresh installation has no saved token yet.
    }

    public OAuthCredentials? Load()
    {
        try
        {
            var plaintext = Protect(File.ReadAllBytes(path), decrypt: true);
            try { return JsonSerializer.Deserialize<OAuthCredentials>(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException) { return null; }
    }

    public void Save(OAuthCredentials credentials)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            var encrypted = Protect(plaintext, decrypt: false);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    // DPAPI is available on Windows without an extra NuGet dependency. Current user only.
    private static byte[] Protect(byte[] bytes, bool decrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = decrypt
                ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Windows 인증 정보 보호에 실패했습니다.");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (var i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
