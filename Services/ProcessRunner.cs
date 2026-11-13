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
        IReadOnlyDictionary<string, string>? environment = null,
        Action<string>? onError = null)
    {
        // Without onError, stderr lines go to onLine with the stdout lines.
        var errorLine = onError ?? onLine;
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
        if (environment is not null)
        {
            foreach (var pair in environment)
                start.Environment[pair.Key] = pair.Value;
        }

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        AppLog.Info($"run {Path.GetFileName(fileName)} {string.Join(' ', start.ArgumentList)}");

        using var process = new Process { StartInfo = start };
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            if (stderr.Length < 16 * 1024)
                stderr.AppendLine(e.Data);
            errorLine?.Invoke(e.Data);
        };

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

        // WaitForExitAsync also waits for the redirected streams to reach EOF,
        // so the last stdout lines are delivered before we read ExitCode.
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var code = process.ExitCode;
        if (code != 0)
            AppLog.Warn($"{Path.GetFileName(fileName)} exited {code}. stderr:\n{stderr}");
        return code;
    }

    /// <summary>stdout and stderr in one text, in arrival order.</summary>
    public static async Task<string> RunTextAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null)
    {
        var (output, _) = await RunCapturedAsync(fileName, arguments, cancellationToken, environment, timeout, split: false)
            .ConfigureAwait(false);
        return output;
    }

    /// <summary>
    /// stdout and stderr apart. A tool that prints data on stdout can print a
    /// notice on stderr at any moment, and a notice inside the data breaks it.
    /// </summary>
    public static Task<(string Output, string Errors)> RunSplitAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null)
        => RunCapturedAsync(fileName, arguments, cancellationToken, environment, timeout, split: true);

    private static async Task<(string Output, string Errors)> RunCapturedAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan? timeout,
        bool split)
    {
        using var linked = timeout is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        linked?.CancelAfter(timeout!.Value);
        var ct = linked?.Token ?? cancellationToken;

        // The two streams arrive on different threads.
        var gate = new object();
        var builder = new StringBuilder();
        var errors = new StringBuilder();
        void Add(StringBuilder target, string line)
        {
            lock (gate)
                target.AppendLine(line);
        }

        try
        {
            var code = await RunAsync(
                fileName,
                arguments,
                line => Add(builder, line),
                ct,
                environment: environment,
                onError: line => Add(split ? errors : builder, line)).ConfigureAwait(false);

            var text = builder.ToString();
            var errorText = errors.ToString();
            if (code != 0 && string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(errorText))
                throw new InvalidOperationException($"{Path.GetFileName(fileName)} exited {code}.");
            return (text, errorText);
        }
        catch (OperationCanceledException) when (timeout is not null && !cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn($"{Path.GetFileName(fileName)} timed out after {timeout.Value.TotalSeconds:0}s.");
            throw new InvalidOperationException("That site took too long. Try again.");
        }
    }
}
