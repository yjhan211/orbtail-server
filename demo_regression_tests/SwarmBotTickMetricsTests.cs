using game_server.services;

namespace demo_regression_tests;

public sealed class SwarmBotTickMetricsTests
{
    [Fact]
    public void BusyPulse_IsCountedWithoutCatchUpAndResetsOnNextRecordedTick()
    {
        var metrics = new SwarmBotTickMetrics();

        metrics.RecordBusySkip();
        metrics.RecordBusySkip();
        Assert.Equal(2, metrics.BusySkips);
        Assert.Equal(2, metrics.ConsecutiveBusySkips);

        // 버린 펄스는 샘플로 남지 않는다 — 다음 기록이 그냥 한 틱이다.
        Assert.Null(metrics.Record(303, new SwarmBotTickSample(1d, 0d, 0d, 0d, 0d)));
        Assert.Equal(1, metrics.SampleCount);
        Assert.Equal(2, metrics.BusySkips);
        Assert.Equal(0, metrics.ConsecutiveBusySkips);
    }

    [Fact]
    public async Task MetricsWindows_AreIsolatedPerMatchDuringConcurrentRecording()
    {
        var first = new SwarmBotTickMetrics();
        var second = new SwarmBotTickMetrics();
        first.RecordBusySkip();
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
        Assert.Equal(1, a.BusySkips);
        Assert.Equal(1, a.MaxConsecutiveBusySkips);
        Assert.Equal(0, b.BusySkips);
        Assert.Equal(0, b.MaxConsecutiveBusySkips);
        // 배치를 내보내면 창과 스킵 카운트가 비워진다.
        Assert.Equal(0, first.SampleCount);
        Assert.Equal(0, first.BusySkips);
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
