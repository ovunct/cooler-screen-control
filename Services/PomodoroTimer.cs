namespace CoolerScreenControl.Services;

public enum PomodoroPhase { Idle, Work, Break }

/// <summary>
/// Simple Pomodoro timer: work, then break, then work again, automatically, until paused or reset.
/// Uses wall-clock times so it stays accurate no matter how often it is polled.
/// </summary>
public sealed class PomodoroTimer
{
    private readonly object _lock = new();
    private PomodoroPhase _phase = PomodoroPhase.Idle;
    private bool _paused;
    private DateTime _endUtc;
    private TimeSpan _remainingWhenPaused;

    public int WorkMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;

    /// <summary>Raised (outside any lock) when a phase ends and the next one begins.</summary>
    public event Action<PomodoroPhase>? PhaseChanged;

    public PomodoroPhase Phase { get { lock (_lock) return _phase; } }
    public bool IsPaused { get { lock (_lock) return _phase != PomodoroPhase.Idle && _paused; } }
    public bool IsRunning { get { lock (_lock) return _phase != PomodoroPhase.Idle && !_paused; } }
    public int CompletedWorkSessions { get; private set; }

    public TimeSpan Remaining
    {
        get
        {
            lock (_lock)
            {
                if (_phase == PomodoroPhase.Idle) return TimeSpan.Zero;
                if (_paused) return _remainingWhenPaused;
                var r = _endUtc - DateTime.UtcNow;
                return r < TimeSpan.Zero ? TimeSpan.Zero : r;
            }
        }
    }

    /// <summary>Starts a new work session, or resumes if paused.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_phase == PomodoroPhase.Idle)
            {
                _phase = PomodoroPhase.Work;
                _paused = false;
                _endUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, WorkMinutes));
            }
            else if (_paused)
            {
                _paused = false;
                _endUtc = DateTime.UtcNow + _remainingWhenPaused;
            }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_phase == PomodoroPhase.Idle || _paused) return;
            _remainingWhenPaused = _endUtc - DateTime.UtcNow;
            if (_remainingWhenPaused < TimeSpan.Zero) _remainingWhenPaused = TimeSpan.Zero;
            _paused = true;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _phase = PomodoroPhase.Idle;
            _paused = false;
            CompletedWorkSessions = 0;
        }
    }

    /// <summary>Call regularly; moves on to the next phase when the current one has run out.</summary>
    public void Update()
    {
        PomodoroPhase? changedTo = null;
        lock (_lock)
        {
            if (_phase == PomodoroPhase.Idle || _paused) return;
            if (DateTime.UtcNow < _endUtc) return;

            if (_phase == PomodoroPhase.Work)
            {
                CompletedWorkSessions++;
                _phase = PomodoroPhase.Break;
                _endUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, BreakMinutes));
            }
            else
            {
                _phase = PomodoroPhase.Work;
                _endUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, WorkMinutes));
            }
            changedTo = _phase;
        }
        PhaseChanged?.Invoke(changedTo.Value);
    }

    /// <summary>The number to put on the cooler: minutes left, or seconds left in the last minute.</summary>
    public int DisplayNumber()
    {
        var r = Remaining;
        return r.TotalSeconds > 60 ? (int)Math.Ceiling(r.TotalMinutes) : (int)Math.Ceiling(r.TotalSeconds);
    }
}
