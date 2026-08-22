using Vaultix.Core;

namespace Vaultix.Core.Tests;

public sealed class TransferMetricsTests
{
    [Fact]
    public void EwmaSmoothsSpikesAndCanBeReset()
    {
        var average = new ExponentiallyWeightedMovingAverage(0.25);
        Assert.Equal(100, average.Add(100));
        Assert.Equal(125, average.Add(200));
        Assert.Equal(93.75, average.Add(0));
        average.Reset();
        Assert.False(average.HasValue);
        Assert.Equal(50, average.Add(50));
    }

    [Fact]
    public void EtaWaitsForReliableSamplesAndSmoothsChanges()
    {
        var eta = new StableEtaEstimator(minimumSamples: 3, alpha: 0.2);
        Assert.Null(eta.Estimate(1_000, 100));
        Assert.Null(eta.Estimate(1_000, 100));
        var stable = eta.Estimate(1_000, 100);
        Assert.NotNull(stable);
        Assert.Equal(10, stable.Value.TotalSeconds);

        var afterSpike = eta.Estimate(1_000, 1_000);
        Assert.NotNull(afterSpike);
        Assert.InRange(afterSpike.Value.TotalSeconds, 8, 9);
    }

    [Fact]
    public void EtaIsUnknownAtZeroSpeedAndZeroWhenComplete()
    {
        var eta = new StableEtaEstimator();
        Assert.Null(eta.Estimate(10_000, 0));
        Assert.Equal(TimeSpan.Zero, eta.Estimate(0, 0));
    }
}
