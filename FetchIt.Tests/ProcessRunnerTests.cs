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
