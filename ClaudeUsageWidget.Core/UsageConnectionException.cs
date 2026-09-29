namespace ClaudeUsageWidget.Core;

public sealed class UsageConnectionException(string message, int status = 0, int? retryAfter = null) : Exception(message)
{
    public int StatusCode { get; } = status;
    public int? RetryAfter { get; } = retryAfter;
}
