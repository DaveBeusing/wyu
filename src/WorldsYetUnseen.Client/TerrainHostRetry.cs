using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

/// <summary>The host may retry only results that prove the request did not take effect.</summary>
internal enum TerrainHostAdmissionResult { Accepted, Busy, Backpressure, Rejected }

internal enum TerrainHostRetryState
{
    Ready, WaitingForProgress, Admitted, Rejected, AttemptsExhausted, DeadlineExpired, Superseded,
}

/// <summary>
/// One host-owned terrain operation. Drive it from the host owner outside authority locks.
/// The request contains the original view, revision and, for an edit, sealed identity.
/// Keep all request data immutable for the lifetime of the operation.
/// </summary>
internal sealed class TerrainHostRetry<TRequest> where TRequest : struct
{
    private readonly TRequest _request;
    private readonly Func<TRequest, TerrainHostAdmissionResult> _admit;
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly TimeSpan _deadline;
    private readonly int _maximumAttempts;
    private ulong _lastTick;
    private ulong _lastProgress;
    private bool _admitting;

    public TerrainHostRetry(VolumeWorkClass path, TRequest request, Func<TRequest, TerrainHostAdmissionResult> admit,
        TimeProvider? clock = null, long? startedTimestamp = null)
    {
        (_maximumAttempts, _deadline) = path switch
        {
            VolumeWorkClass.Movement => (3, TimeSpan.FromSeconds(3)),
            VolumeWorkClass.FirstEdit => (2, TimeSpan.FromMilliseconds(3250)),
            VolumeWorkClass.Recovery => (3, TimeSpan.FromSeconds(4)),
            _ => throw new ArgumentOutOfRangeException(nameof(path), "Only protected terrain paths have a host retry budget."),
        };
        _request = request;
        _admit = admit ?? throw new ArgumentNullException(nameof(admit));
        _clock = clock ?? TimeProvider.System;
        // A supplied timestamp is the operation start in the same monotonic clock domain.
        _started = startedTimestamp ?? _clock.GetTimestamp();
    }

    public TerrainHostRetryState State { get; private set; }
    public int Attempts { get; private set; }
    public int TransientDenials { get; private set; }
    public TerrainHostAdmissionResult? LastAdmission { get; private set; }

    /// <summary>Checks the admission deadline measured from the original operation start.</summary>
    public bool IsDeadlineElapsed => _clock.GetElapsedTime(_started) >= _deadline;

    /// <summary>
    /// Attempt admission on an eligible tick. Advance progressVersion only when the owner observes new
    /// progress or returned credit. Acceptance ends retries and transfers custody to the caller,
    /// including when the admission call crosses the deadline. The caller owns normal settlement.
    /// </summary>
    public TerrainHostRetryState Advance(ulong tick, ulong progressVersion)
    {
        if (_admitting)
            throw new InvalidOperationException("Terrain admission cannot reenter its host operation.");
        if (State is not (TerrainHostRetryState.Ready or TerrainHostRetryState.WaitingForProgress))
            return State;
        if (IsDeadlineElapsed)
            return State = TerrainHostRetryState.DeadlineExpired;
        if (Attempts != 0 && (tick <= _lastTick || progressVersion <= _lastProgress))
            return State;

        _lastTick = tick;
        _lastProgress = progressVersion;
        Attempts++;
        _admitting = true;
        // An exception or unknown result cannot prove that an attempt had no effect.
        State = TerrainHostRetryState.Rejected;
        try
        {
            LastAdmission = _admit(_request);
            switch (LastAdmission)
            {
                case TerrainHostAdmissionResult.Accepted:
                    return State = TerrainHostRetryState.Admitted;
                case TerrainHostAdmissionResult.Busy:
                case TerrainHostAdmissionResult.Backpressure:
                    TransientDenials++;
                    return State = IsDeadlineElapsed ? TerrainHostRetryState.DeadlineExpired :
                        Attempts == _maximumAttempts ? TerrainHostRetryState.AttemptsExhausted :
                        TerrainHostRetryState.WaitingForProgress;
                default:
                    return State;
            }
        }
        finally
        {
            _admitting = false;
        }
    }

    /// <summary>Use when the view, source revision, or sealed identity is superseded before admission.</summary>
    public void Supersede()
    {
        if (_admitting)
            throw new InvalidOperationException("Terrain admission cannot reenter its host operation.");
        if (State is TerrainHostRetryState.Ready or TerrainHostRetryState.WaitingForProgress)
            State = TerrainHostRetryState.Superseded;
    }

}

internal static class TerrainHostAdmission
{
    public static TerrainHostAdmissionResult Classify(VolumeAdmission result) => result switch
    {
        VolumeAdmission.Accepted => TerrainHostAdmissionResult.Accepted,
        VolumeAdmission.Backpressure => TerrainHostAdmissionResult.Backpressure,
        _ => TerrainHostAdmissionResult.Rejected,
    };
}
