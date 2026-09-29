using System.IO.Pipes;
using ClaudeUsageWidget.Core;

// Chrome supplies the allowed extension origin as argv[0]. Never write diagnostics to stdout.
if (args.Length == 0 || args[0] != NativeProtocol.Origin) return 2;
try
{
    using var pipe = new NamedPipeClientStream(".", NativeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var stop = new CancellationTokenSource();
    await pipe.ConnectAsync(10000, stop.Token);
    await NativeProtocol.WriteAsync(pipe, new { type = "hello", origin = NativeProtocol.Origin }, stop.Token);
    using var input = Console.OpenStandardInput();
    using var output = Console.OpenStandardOutput();
    async Task Pump(Stream from, Stream to)
    {
        while (!stop.IsCancellationRequested)
        {
            var message = await NativeProtocol.ReadAsync(from, stop.Token);
            await NativeProtocol.WriteAsync(to, message, stop.Token);
        }
    }
    var incoming = Pump(input, pipe);
    var outgoing = Pump(pipe, output);
    await Task.WhenAny(incoming, outgoing);
    stop.Cancel();
    return 0;
}
catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or System.Text.Json.JsonException)
{ return 1; }
