using game_server.players.bots;

namespace demo_regression_tests;

public sealed class BotMovementMetricsTests
{
    [Fact]
    public void CompletedTick_IsRecordedAsOneSample()
    {
        var metrics = new BotMovementMetrics();
        Assert.Null(metrics.Record(303, new BotMovementSample(1d, 0d, 0d, 0d, 0d)));
        Assert.Equal(1, metrics.SampleCount);
    }
    [Fact]
    public async Task MetricsWindows_AreIsolatedPerMatchDuringConcurrentRecording()
    {
        var first = new BotMovementMetrics();
        var second = new BotMovementMetrics();
        BotMovementMetricsBatch? firstBatch = null;
        BotMovementMetricsBatch? secondBatch = null;

        await Task.WhenAll(
            Task.Run(() => firstBatch = RecordWindow(first, 505, 1d)),
            Task.Run(() => secondBatch = RecordWindow(second, 606, 2d)));

        BotMovementMetricsBatch a = Assert.IsType<BotMovementMetricsBatch>(firstBatch);
        BotMovementMetricsBatch b = Assert.IsType<BotMovementMetricsBatch>(secondBatch);
        Assert.Equal(505, a.MatchingId);
        Assert.Equal(606, b.MatchingId);
        Assert.Equal(BotMovementMetrics.WindowSize, a.TickSamples.Length);
        Assert.Equal(BotMovementMetrics.WindowSize, b.TickSamples.Length);
        Assert.All(a.TickSamples, value => Assert.Equal(1d, value));
        Assert.All(b.TickSamples, value => Assert.Equal(2d, value));
        // 배치를 내보내면 샘플 창이 비워진다.
        Assert.Equal(0, first.SampleCount);
    }

    private static BotMovementMetricsBatch? RecordWindow(
        BotMovementMetrics metrics,
        long matchingId,
        double elapsed)
    {
        BotMovementMetricsBatch? batch = null;
        for (int sample = 0; sample < BotMovementMetrics.WindowSize; sample++)
        {
            batch = metrics.Record(
                matchingId,
                new BotMovementSample(elapsed, elapsed, elapsed, elapsed, elapsed));
        }

        return batch;
    }
}
