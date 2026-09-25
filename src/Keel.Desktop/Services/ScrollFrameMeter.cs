using System.Diagnostics;
using Keel.Application.Stats;

namespace Keel.Desktop.Services;

/// <summary>
/// Times the frames the window draws while a register is scrolling (PRD 4: 60 fps at 100k rows, ADR 0102). The
/// register's <c>RegisterSource</c> reports every row the grid asks for; the first request after a quiet spell
/// starts a frame loop (one animation-frame callback per frame) that runs until no row was requested for
/// <see cref="QuietPeriod"/>. Each interval between two frames is one frame time. Cheap: nothing runs while the
/// register is idle, and a burst costs one callback per frame.
/// </summary>
public sealed class ScrollFrameMeter
{
    /// <summary>A burst ends after this long without row requests.</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(250);

    /// <summary>Frames needed before a session counts.</summary>
    public const int MinimumFrames = 30;

    /// <summary>Frame times kept per session (the latest).</summary>
    public const int MaxFrames = 5_000;

    private readonly Action<Action<TimeSpan>> _requestFrame;
    private readonly Func<long> _clock;
    private readonly List<double> _frames = [];
    private long _lastActivity;
    private TimeSpan? _lastFrame;
    private bool _running;

    /// <summary>Creates a meter over a frame scheduler (the window's <c>RequestAnimationFrame</c>).</summary>
    public ScrollFrameMeter(Action<Action<TimeSpan>> requestFrame, Func<long>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(requestFrame);
        _requestFrame = requestFrame;
        _clock = clock ?? Stopwatch.GetTimestamp;
    }

    /// <summary>Raised on the UI thread when a burst ends.</summary>
    public event EventHandler? BurstEnded;

    /// <summary>Frame times measured in this session, in milliseconds.</summary>
    public IReadOnlyList<double> Frames => _frames;

    /// <summary>Whether a frame loop is running.</summary>
    public bool IsRunning => _running;

    /// <summary>The grid asked for a row: keep (or start) timing frames.</summary>
    public void Activity()
    {
        _lastActivity = _clock();
        if (!_running)
        {
            _running = true;
            _lastFrame = null;
            _requestFrame(OnFrame);
        }
    }

    /// <summary>Clears the session (another register or account).</summary>
    public void Reset()
    {
        _frames.Clear();
        _lastFrame = null;
    }

    /// <summary>One animation frame at <paramref name="time"/> (public for tests).</summary>
    public void OnFrame(TimeSpan time)
    {
        if (_lastFrame is { } last && time > last)
        {
            if (_frames.Count == MaxFrames)
            {
                _frames.RemoveAt(0);
            }

            _frames.Add((time - last).TotalMilliseconds);
        }

        _lastFrame = time;
        if (Stopwatch.GetElapsedTime(_lastActivity, _clock()) < QuietPeriod)
        {
            _requestFrame(OnFrame);
            return;
        }

        _running = false;
        _lastFrame = null;
        BurstEnded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The session as a sample, or null with fewer than <see cref="MinimumFrames"/> frames.</summary>
    public RegisterScrollSample? Summarize(int rows, DateTime measuredAtUtc) => Summarize(_frames, rows, measuredAtUtc);

    /// <summary>Average and 95th-percentile frame time of <paramref name="frames"/>.</summary>
    public static RegisterScrollSample? Summarize(IReadOnlyList<double> frames, int rows, DateTime measuredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count < MinimumFrames)
        {
            return null;
        }

        var sorted = frames.Order().ToArray();
        var p95 = sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.95) - 1)];
        return new RegisterScrollSample(rows, frames.Count, Math.Round(frames.Average(), 2), Math.Round(p95, 2), measuredAtUtc);
    }
}
