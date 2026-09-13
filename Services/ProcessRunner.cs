using System.Diagnostics;
using System.Text;

namespace FetchIt.Services;

internal static class ProcessRunner
{
    public static async Task<int> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<string>? onLine,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            start.WorkingDirectory = workingDirectory;
        if (environment is not null)
        {
            foreach (var pair in environment)
                start.Environment[pair.Key] = pair.Value;
        }

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                onLine?.Invoke(e.Data);
        };
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        if (!process.Start())
            throw new InvalidOperationException($"Could not start {fileName}.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        });

        return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> RunTextAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null)
    {
        using var linked = timeout is { } limit
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        linked?.CancelAfter(timeout!.Value);
        var ct = linked?.Token ?? cancellationToken;

        var builder = new StringBuilder();
        try
        {
            var code = await RunAsync(fileName, arguments, line =>
            {
                builder.AppendLine(line);
            }, ct, environment: environment).ConfigureAwait(false);

            var text = builder.ToString();
            if (code != 0 && string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException($"{Path.GetFileName(fileName)} exited {code}.");
            return text;
        }
        catch (OperationCanceledException) when (timeout is not null && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("That site took too long. Try again.");
        }
    }
}
