using network.common.data.models;
using user_server.matching.creation;
using user_server.matching.queue;

namespace user_server.matching;

/// <summary>
///     매치 구성 저장, 입장 티켓과 매칭 결과 전달, 입장 준비 및 실패 정리를 제공한다.
///     매치 생성 흐름을 실제 Redis·티켓 발급·세션 전달 없이 테스트할 수 있도록 인터페이스로 분리.
/// </summary>
internal interface IMatchEntryService
{
    public Task StoreMatchManifestAsync(long matchingId, MatchManifest manifest);

    public Task<bool> DeliverMatchingSuccessAsync(
        MatchingQueueData request,
        long matchingId,
        GameServerAllocation gameServer);

    public Task MarkEntryReadyAsync(long matchingId);
    public bool StartEntryTimeoutCheck(long matchingId, IReadOnlyCollection<long> humanPlayerIds);
    public Task<bool> TryCancelEntryForRollbackAsync(long matchingId);
    public Task DeleteMatchEntryDataAsync(long matchingId);
    public Task NotifyBatchFailedAsync(IEnumerable<MatchingQueueData> players, long matchingId);
}
