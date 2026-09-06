using network.common.data.models;
using user_server.matching.creation;
using user_server.matching.queue;

namespace user_server.matching;

/// <summary>
///     매치 확정 뒤 Game Server 인계에 필요한 쓰기와 클라이언트 전달을 담당하는 port.
///     <see cref="MatchCreationService" />가 테스트에서 전달·마커 순서를 관찰할 수 있도록 분리한다.
/// </summary>
internal interface IMatchEntryService
{
    public Task StoreMatchManifestAsync(long matchingId, MatchManifest manifest);

    public Task<bool> DeliverMatchingSuccessAsync(
        MatchingQueueData request,
        long matchingId,
        GameServerAllocation gameServer);

    public Task MarkEntryReadyAsync(long matchingId);
    public bool StartEntryWatchdog(long matchingId, IReadOnlyCollection<long> humanPlayerIds);
    public Task<bool> TryCancelEntryForRollbackAsync(long matchingId);
    public Task DeleteHandoffBestEffortAsync(long matchingId);
    public Task NotifyBatchFailedAsync(IEnumerable<MatchingQueueData> players, long matchingId);
}
