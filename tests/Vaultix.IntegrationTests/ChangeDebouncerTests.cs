using Vaultix.Service;

namespace Vaultix.IntegrationTests;

public sealed class ChangeDebouncerTests
{
    [Fact]
    public void NewEventsRestartTheQuietPeriod()
    {
        var collector = new ChangeDebouncer();
        var folder = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;
        collector.Mark(folder, start);
        Assert.Empty(collector.DequeueDue(start.AddSeconds(9), TimeSpan.FromSeconds(10)));

        collector.Mark(folder, start.AddSeconds(9));
        Assert.Empty(collector.DequeueDue(start.AddSeconds(10), TimeSpan.FromSeconds(10)));
        Assert.Equal(folder, Assert.Single(collector.DequeueDue(start.AddSeconds(19), TimeSpan.FromSeconds(10))));
        Assert.Empty(collector.DequeueDue(start.AddSeconds(30), TimeSpan.FromSeconds(10)));
    }
}
