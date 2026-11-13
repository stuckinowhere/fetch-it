using FetchIt.Services;

namespace FetchIt.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task RunTextAsync_keeps_every_stdout_line_until_exit()
    {
        // Regression: resolving on Process.Exited dropped the tail of stdout
        // and produced truncated yt-dlp JSON ("invalid start of a value").
        const int lines = 3000;
        var text = await ProcessRunner.RunTextAsync(
            "cmd.exe",
            ["/c", $"for /L %i in (1,1,{lines}) do @echo line%i"],
            CancellationToken.None,
            timeout: TimeSpan.FromSeconds(30));

        var got = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(lines, got.Length);
        Assert.Equal("line1", got[0]);
        Assert.Equal($"line{lines}", got[^1]);
    }

    [Fact]
    public async Task RunSplitAsync_keeps_stderr_notices_out_of_the_output()
    {
        // Regression: gallery-dl wrote "[twitter][info] Requesting guest token"
        // on stderr before the JSON, and the mixed text could not be parsed.
        var (output, errors) = await ProcessRunner.RunSplitAsync(
            "cmd.exe",
            ["/c", "echo [twitter][info] Requesting guest token 1>&2 & echo [[3]]"],
            CancellationToken.None,
            timeout: TimeSpan.FromSeconds(30));

        Assert.Equal("[[3]]", output.Trim());
        Assert.Contains("Requesting guest token", errors);
    }

    [Fact]
    public async Task RunTextAsync_still_returns_stderr_with_stdout()
    {
        var text = await ProcessRunner.RunTextAsync(
            "cmd.exe",
            ["/c", "echo notice 1>&2 & echo data"],
            CancellationToken.None,
            timeout: TimeSpan.FromSeconds(30));

        Assert.Contains("notice", text);
        Assert.Contains("data", text);
    }

    [Fact]
    public async Task RunAsync_returns_exit_code()
    {
        var code = await ProcessRunner.RunAsync("cmd.exe", ["/c", "exit 7"], null, CancellationToken.None);
        Assert.Equal(7, code);
    }

    [Fact]
    public async Task RunTextAsync_timeout_maps_to_friendly_error()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProcessRunner.RunTextAsync(
                "cmd.exe",
                ["/c", "ping -n 10 127.0.0.1 > nul"],
                CancellationToken.None,
                timeout: TimeSpan.FromMilliseconds(300)));
        Assert.Equal("That site took too long. Try again.", ex.Message);
    }
}
