namespace AntLslStreamer;

/// <summary>
/// Assigns LSL timestamps from a sample counter instead of the chunk arrival time.
///
/// Stamping every chunk with its arrival time copies USB / SDK delivery jitter into the timestamps and
/// can make them step backwards between chunks. Here sample k of the run gets
/// <c>t0 + k / rate</c>, where the offset <c>t0</c> is estimated from arrival times: first as a running
/// mean (fast convergence at start-up), then with a small gain and a per-chunk step limit so that it
/// follows the slow drift between the amplifier and PC clocks but not the delivery jitter.
/// Timestamps are guaranteed to increase monotonically. After sample loss, or when the arrival times
/// disagree with the model by more than <see cref="ResyncThreshold"/>, the clock is re-anchored.
/// </summary>
internal sealed class SampleClock
{
    // All constants are in time, so the behaviour does not depend on the chunk size (buffer / ready %).
    private const double WarmupSeconds = 5;        // running mean of the offset over the first seconds
    private const double TrackingTau = 20;         // s; afterwards: time constant of the offset tracking
    private const double MaxSlewPerSecond = 1e-3;  // max correction rate (s/s = 1000 ppm, >> real clock drift)
    public const double ResyncThreshold = 0.5;     // s; larger errors re-anchor the clock

    private readonly double _rate;
    private double _t0;            // LSL time of sample 0 (of the current anchor)
    private long _n;               // samples stamped since the anchor
    private int _chunks;           // chunks seen since the anchor
    private double _lastTs = double.NegativeInfinity;
    private bool _anchored;

    public SampleClock(double rate) => _rate = rate;

    /// <summary>Last arrival-time error (arrival − model) in seconds; positive = chunk arrived late.</summary>
    public double LastError { get; private set; }

    /// <summary>Largest |error| since the last call to <see cref="TakeMaxAbsError"/>.</summary>
    private double _maxAbsError;
    public double TakeMaxAbsError() { double v = _maxAbsError; _maxAbsError = 0; return v; }

    public int Resyncs { get; private set; }

    /// <summary>Forget the anchor (e.g. after sample loss); the next chunk starts a new one.</summary>
    public void Reset() => _anchored = false;

    /// <summary>
    /// Returns the LSL timestamp of the first sample of a chunk of <paramref name="count"/> samples whose
    /// last sample was received at LSL time <paramref name="arrival"/>.
    /// </summary>
    public double Stamp(int count, double arrival)
    {
        double period = 1.0 / _rate;
        if (!_anchored)
        {
            Anchor(count, arrival, period);
        }
        else
        {
            double predictedLast = _t0 + (_n + count - 1) * period;
            double err = arrival - predictedLast;
            LastError = err;
            _maxAbsError = Math.Max(_maxAbsError, Math.Abs(err));

            if (Math.Abs(err) > ResyncThreshold)
            {
                Log.Warn($"Sample clock: chunk arrival differs from the sample count by {err * 1000:F0} ms; re-anchoring.");
                Resyncs++;
                Anchor(count, arrival, period);
            }
            else
            {
                _chunks++;
                double chunkSeconds = count * period;
                double corr;
                if (_n * period < WarmupSeconds)
                {
                    corr = err / _chunks; // running mean of the offset
                }
                else
                {
                    corr = err * Math.Min(1.0, chunkSeconds / TrackingTau);
                    double maxStep = MaxSlewPerSecond * chunkSeconds;
                    corr = Math.Clamp(corr, -maxStep, maxStep);
                }
                _t0 += corr;
            }
        }

        double first = _t0 + _n * period;
        // Never step backwards (can only matter during warm-up / after a re-anchor).
        double minFirst = _lastTs + 0.5 * period;
        if (first < minFirst)
        {
            _t0 += minFirst - first;
            first = minFirst;
        }
        _n += count;
        _lastTs = first + (count - 1) * period;
        return first;
    }

    private void Anchor(int count, double arrival, double period)
    {
        _t0 = arrival - (count - 1) * period;
        _n = 0;
        _chunks = 1;
        LastError = 0;
        _anchored = true;
    }
}
