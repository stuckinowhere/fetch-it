using System.Globalization;
using System.Text.RegularExpressions;

namespace FetchIt.Services;

/// <summary>
/// Follows gallery-dl while it lists a profile: one line per file found, and
/// bracketed log lines for everything else.
/// </summary>
internal sealed partial class ProfileListing
{
    private const int MaxLoggedLines = 40;

    private readonly object _gate = new();
    private readonly List<string> _problems = [];
    private readonly Func<DateTime> _now;
    private int _files;
    private int _logged;
    private string? _waitingUntil;
    private DateTime _lastLine;

    public ProfileListing(Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        _lastLine = _now();
    }

    [GeneratedRegex(@"Waiting until (\d{1,2}:\d{2}(?::\d{2})?)")]
    private static partial Regex WaitingLine();

    public int Files
    {
        get
        {
            lock (_gate)
                return _files;
        }
    }

    /// <summary>What gallery-dl complained about, to explain a run that found nothing.</summary>
    public string Problems
    {
        get
        {
            lock (_gate)
                return string.Join('\n', _problems);
        }
    }

    public void Read(string line)
    {
        lock (_gate)
        {
            _lastLine = _now();
            if (!line.StartsWith('['))
            {
                _files++;
                _waitingUntil = null;
                return;
            }

            if (WaitingLine().Match(line) is { Success: true } waiting)
                _waitingUntil = waiting.Groups[1].Value;

            if (line.Contains("][error]", StringComparison.OrdinalIgnoreCase)
                || line.Contains("][warning]", StringComparison.OrdinalIgnoreCase))
                Keep(line);
        }
    }

    private void Keep(string line)
    {
        if (_logged >= MaxLoggedLines)
            return;
        _logged++;
        _problems.Add(line);
        AppLog.Warn($"profile listing: {line[..Math.Min(line.Length, 200)]}");
    }

    /// <summary>True when gallery-dl has said nothing for longer than the limit.</summary>
    public bool IsStalled(TimeSpan limit)
    {
        lock (_gate)
            return _now() - _lastLine > limit;
    }

    public string Status(TimeSpan elapsed)
    {
        var clock = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
        lock (_gate)
        {
            var files = _files.ToString("N0", CultureInfo.CurrentCulture);
            return _waitingUntil is { } until
                ? L.Format("Instagram asked to wait until {0}. It continues by itself. {1} files · {2}", until, files, clock)
                : L.Format("Reading the whole profile… {0} files · {1}", files, clock);
        }
    }
}
