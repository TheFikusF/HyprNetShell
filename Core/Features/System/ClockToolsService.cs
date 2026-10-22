using System.Diagnostics;
using HyprNetShell.Core.Logging;

namespace HyprNetShell.Core.Features.System;

internal sealed record ClockAlarm(Guid Id, TimeOnly Time, DateTime NextOccurrence);

internal sealed class ClockToolsService : IDisposable
{
    private static readonly TimeSpan CalendarNotificationWindow = TimeSpan.FromMinutes(1);
    private readonly Lock _gate = new();
    private readonly CalendarService _calendar;
    private readonly NotificationService _notifications;
    private readonly List<ClockAlarm> _alarms = [];
    private readonly HashSet<(string Source, DateTime Start, string Title)> _notifiedOccurrences = [];
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly Stopwatch _timer = new();
    private readonly Stopwatch _stopwatch = new();
    private static ClockToolsService? _shared;
    private TimeSpan? _timerDuration;
    private bool _stopwatchActive;
    private bool _disposed;
    private DateTime _lastCalendarCheck = DateTime.Now;

    internal static ClockToolsService Shared => Volatile.Read(ref _shared)
        ?? throw new InvalidOperationException("Clock tools have not been initialized.");

    public TimeSpan? TimerRemaining
    {
        get
        {
            lock (_gate)
            {
                return _timerDuration is { } duration ? MaxZero(duration - _timer.Elapsed) : null;
            }
        }
    }

    public bool TimerRunning
    {
        get
        {
            lock (_gate)
            {
                return _timer.IsRunning;
            }
        }
    }

    public TimeSpan StopwatchElapsed
    {
        get
        {
            lock (_gate)
            {
                return _stopwatch.Elapsed;
            }
        }
    }

    public bool StopwatchRunning
    {
        get
        {
            lock (_gate)
            {
                return _stopwatch.IsRunning;
            }
        }
    }

    public bool StopwatchActive
    {
        get
        {
            lock (_gate)
            {
                return _stopwatchActive;
            }
        }
    }

    public IReadOnlyList<ClockAlarm> Alarms
    {
        get
        {
            lock (_gate)
            {
                return _alarms.OrderBy(alarm => alarm.NextOccurrence).ToArray();
            }
        }
    }

    internal ClockToolsService(CalendarService calendar, NotificationService notifications)
    {
        _calendar = calendar;
        _notifications = notifications;
        Volatile.Write(ref _shared, this);
    }

    public void StartTimer(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _timerDuration = duration;
            _timer.Restart();
        }
    }

    public void PauseTimer()
    {
        lock (_gate)
        {
            _timer.Stop();
        }
    }

    public void ResumeTimer()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timerDuration is { } duration && _timer.Elapsed < duration)
            {
                _timer.Start();
            }
        }
    }

    public void ResetTimer()
    {
        lock (_gate)
        {
            _timer.Reset();
            _timerDuration = null;
        }
    }

    public void StartStopwatch()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stopwatchActive = true;
            _stopwatch.Start();
        }
    }

    public void PauseStopwatch()
    {
        lock (_gate)
        {
            _stopwatch.Stop();
        }
    }

    public void ResetStopwatch()
    {
        lock (_gate)
        {
            _stopwatch.Reset();
            _stopwatchActive = false;
        }
    }

    public void AddAlarm(TimeOnly time)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = DateTime.Now;
            var next = now.Date.Add(time.ToTimeSpan());
            if (next <= now)
            {
                next = next.AddDays(1);
            }

            _alarms.Add(new ClockAlarm(Guid.NewGuid(), time, next));
        }
    }

    public void RemoveAlarm(Guid id)
    {
        lock (_gate)
        {
            _alarms.RemoveAll(alarm => alarm.Id == id);
        }
    }

    internal void Tick()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var now = DateTime.Now;
            if (_timer.IsRunning && _timerDuration is { } duration && _timer.Elapsed >= duration)
            {
                _timer.Reset();
                _timerDuration = null;
                Notify("Timer complete", "Your timer has finished.");
            }

            foreach (var alarm in _alarms.Where(alarm => alarm.NextOccurrence <= now).ToArray())
            {
                _alarms.Remove(alarm);
                Notify("Alarm", alarm.Time.ToString("HH:mm"));
            }

            // A short catch-up window tolerates delayed frames without replaying old events.
            var since = now - CalendarNotificationWindow;
            if (_lastCalendarCheck > since)
            {
                since = _lastCalendarCheck;
            }

            if (_startedAt > since)
            {
                since = _startedAt;
            }

            foreach (var occurrence in _calendar.Snapshot.Occurrences)
            {
                var start = occurrence.Start.Kind == DateTimeKind.Utc
                    ? occurrence.Start.ToLocalTime()
                    : occurrence.Start;
                if (!occurrence.IsAllDay && start > since && start <= now &&
                    _notifiedOccurrences.Add((occurrence.SourceUrl, start, occurrence.Title)))
                {
                    Notify("Calendar event", occurrence.Title);
                }
            }

            _lastCalendarCheck = now;
        }
    }

    private void Notify(string title, string body)
    {
        try
        {
            _notifications.ShowLocal(title, body);
        }

        catch (Exception exception)
        {
            AppLogger.Warning("ClockTools", $"Could not show {title} notification", exception);
        }
    }

    private static TimeSpan MaxZero(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Stop();
            _stopwatch.Stop();
            _alarms.Clear();
            _notifiedOccurrences.Clear();
            Interlocked.CompareExchange(ref _shared, null, this);
        }
    }
}
