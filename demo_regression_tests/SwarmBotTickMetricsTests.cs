using game_server.players.bots;

namespace demo_regression_tests;

public sealed class SwarmBotTickMetricsTests
{
    [Fact]
    public void CompletedTick_IsRecordedAsOneSample()
    {
        var metrics = new SwarmBotTickMetrics();
        Assert.Null(metrics.Record(303, new SwarmBotTickSample(1d, 0d, 0d, 0d, 0d)));
        Assert.Equal(1, metrics.SampleCount);
    }
    [Fact]
    public async Task MetricsWindows_AreIsolatedPerMatchDuringConcurrentRecording()
    {
        var first = new SwarmBotTickMetrics();
        var second = new SwarmBotTickMetrics();
        SwarmBotTickMetricsBatch? firstBatch = null;
        SwarmBotTickMetricsBatch? secondBatch = null;

        await Task.WhenAll(
            Task.Run(() => firstBatch = RecordWindow(first, 505, 1d)),
            Task.Run(() => secondBatch = RecordWindow(second, 606, 2d)));

        SwarmBotTickMetricsBatch a = Assert.IsType<SwarmBotTickMetricsBatch>(firstBatch);
        SwarmBotTickMetricsBatch b = Assert.IsType<SwarmBotTickMetricsBatch>(secondBatch);
        Assert.Equal(505, a.MatchingId);
        Assert.Equal(606, b.MatchingId);
        Assert.Equal(SwarmBotTickMetrics.WindowSize, a.TickSamples.Length);
        Assert.Equal(SwarmBotTickMetrics.WindowSize, b.TickSamples.Length);
        Assert.All(a.TickSamples, value => Assert.Equal(1d, value));
        Assert.All(b.TickSamples, value => Assert.Equal(2d, value));
        // 배치를 내보내면 샘플 창이 비워진다.
        Assert.Equal(0, first.SampleCount);
    }

    private static SwarmBotTickMetricsBatch? RecordWindow(
        SwarmBotTickMetrics metrics,
        long matchingId,
        double elapsed)
    {
        SwarmBotTickMetricsBatch? batch = null;
        for (int sample = 0; sample < SwarmBotTickMetrics.WindowSize; sample++)
        {
            batch = metrics.Record(
                matchingId,
                new SwarmBotTickSample(elapsed, elapsed, elapsed, elapsed, elapsed));
        }

        return batch;
    }
}
