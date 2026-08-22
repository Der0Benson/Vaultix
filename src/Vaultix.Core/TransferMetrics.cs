namespace Vaultix.Core;

public sealed class ExponentiallyWeightedMovingAverage(double alpha)
{
    private readonly double _alpha = alpha is > 0 and <= 1
        ? alpha
        : throw new ArgumentOutOfRangeException(nameof(alpha));
    private double _value;

    public bool HasValue { get; private set; }
    public double Value => HasValue ? _value : 0;

    public double Add(double sample)
    {
        if (double.IsNaN(sample) || double.IsInfinity(sample) || sample < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sample));
        }

        _value = HasValue ? (_alpha * sample) + ((1 - _alpha) * _value) : sample;
        HasValue = true;
        return _value;
    }

    public void Reset()
    {
        _value = 0;
        HasValue = false;
    }
}

public sealed class StableEtaEstimator(int minimumSamples = 3, double alpha = 0.2)
{
    private readonly ExponentiallyWeightedMovingAverage _eta = new(alpha);
    private int _positiveSamples;

    public TimeSpan? Estimate(long remainingBytes, double smoothedBytesPerSecond)
    {
        if (remainingBytes <= 0)
        {
            return TimeSpan.Zero;
        }

        if (smoothedBytesPerSecond < 1)
        {
            _positiveSamples = 0;
            _eta.Reset();
            return null;
        }

        _positiveSamples++;
        var seconds = remainingBytes / smoothedBytesPerSecond;
        var stableSeconds = _eta.Add(seconds);
        return _positiveSamples < minimumSamples || stableSeconds > TimeSpan.FromDays(30).TotalSeconds
            ? null
            : TimeSpan.FromSeconds(stableSeconds);
    }

    public void Reset()
    {
        _positiveSamples = 0;
        _eta.Reset();
    }
}
