using System.Text.Json;
using Microsoft.Extensions.Logging;
using network.common;
using network.helpers;
using network.interfaces;
#pragma warning disable CS8618

namespace game_server.services;

/// <summary>
///     글로벌 매칭 설정 서비스 (싱글톤).
///     다음 매칭부터 적용되는 폐쇄 config + 직책 풀 config를 관리한다.
///     폐쇄 config는 메모리, 직책 풀 config는 Redis에 저장하여 user_server와 공유한다.
///     진행 중인 인스턴스에는 영향을 주지 않는다.
/// </summary>
public class MatchingConfigService
{
    // Redis 공유 키 (user_server도 동일 키를 읽는다)
    public const string JobPoolRedisKey = "matching_config";
    public const string JobPoolRedisField = "job_pool";
    public const string StartDelayRedisField = "start_delay_sec";
    public const string IntervalRedisField = "interval_sec";
    public const string ForcedSequenceRedisField = "forced_sequence";

    private readonly ICacheHelper _cacheHelper;
    private readonly ILogger _logger;

    // 폐쇄 config (메모리 캐시 — Redis와 동기화)
    private int _startDelaySec = DefaultStartDelaySec;
    private int _intervalSec = DefaultIntervalSec;
    private List<AreaType>? _forcedSequence; // null = 무작위

    public const int DefaultStartDelaySec = 150;
    public const int DefaultIntervalSec = 90;

    public MatchingConfigService(ICacheHelper cacheHelper, ILogger logger)
    {
        _cacheHelper = cacheHelper;
        _logger = logger;
    }

    // ─── 폐쇄 Config ────────────────────────────────────────────────────────

    public ClosureConfig GetClosureConfig()
    {
        if (DemoMode.IsActive)
        {
            return new ClosureConfig
            {
                StartDelaySec = DemoMode.ClosureStartDelaySec,
                IntervalSec = DemoMode.ClosureIntervalSec,
                ForcedSequence = new List<AreaType>(DemoMode.ForcedClosureSequence)
            };
        }

        return new ClosureConfig
        {
            StartDelaySec = _startDelaySec,
            IntervalSec = _intervalSec,
            ForcedSequence = _forcedSequence != null ? new List<AreaType>(_forcedSequence) : null
        };
    }

    /// <summary>
    ///     서버 시작 시 Redis에서 폐쇄 config 로드 (재시작/핫리로드 후에도 보존).
    ///     키가 없으면 기본값 유지.
    /// </summary>
    public async Task LoadClosureConfigFromRedisAsync()
    {
        try
        {
            var startRaw = await _cacheHelper.HashGetAsync(JobPoolRedisKey, StartDelayRedisField);
            if (startRaw.HasValue && int.TryParse((string)startRaw!, out int sd))
                _startDelaySec = Math.Max(0, sd);

            var intervalRaw = await _cacheHelper.HashGetAsync(JobPoolRedisKey, IntervalRedisField);
            if (intervalRaw.HasValue && int.TryParse((string)intervalRaw!, out int iv))
                _intervalSec = Math.Max(1, iv);

            var seqRaw = await _cacheHelper.HashGetAsync(JobPoolRedisKey, ForcedSequenceRedisField);
            if (seqRaw.HasValue)
            {
                var ints = JsonSerializer.Deserialize<List<int>>((string)seqRaw!);
                _forcedSequence = ints?.Select(v => (AreaType)v).ToList();
            }

            // DEMO_MODE에서는 Redis에 남은 이전 설정보다 시연용 폐쇄 기본값을 우선한다.
            if (DemoMode.IsActive)
            {
                _startDelaySec = DemoMode.ClosureStartDelaySec;
                _intervalSec = DemoMode.ClosureIntervalSec;
                _forcedSequence = new List<AreaType>(DemoMode.ForcedClosureSequence);
                _logger.LogInformation("DEMO_MODE 활성 — 폐쇄 config를 시연 기본값으로 고정");
            }

            _logger.LogInformation(
                "Redis에서 폐쇄 config 로드 — startDelaySec={StartDelay}, intervalSec={Interval}, forcedSequence={Seq}",
                _startDelaySec, _intervalSec,
                _forcedSequence != null ? string.Join(",", _forcedSequence) : "무작위");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis 폐쇄 config 로드 실패 — 기본값 사용");
        }
    }

    /// <summary>
    ///     폐쇄 config 부분 업데이트. null 인자는 변경하지 않는다. Redis에 영속화.
    /// </summary>
    public async Task SetClosureConfigAsync(int? startDelaySec, int? intervalSec, List<int>? sequence)
    {
        if (startDelaySec.HasValue)
        {
            _startDelaySec = Math.Max(0, startDelaySec.Value);
            await _cacheHelper.HashSetAsync(JobPoolRedisKey, StartDelayRedisField,
                System.Text.Encoding.UTF8.GetBytes(_startDelaySec.ToString()));
        }

        if (intervalSec.HasValue)
        {
            _intervalSec = Math.Max(1, intervalSec.Value);
            await _cacheHelper.HashSetAsync(JobPoolRedisKey, IntervalRedisField,
                System.Text.Encoding.UTF8.GetBytes(_intervalSec.ToString()));
        }

        if (sequence != null)
        {
            _forcedSequence = sequence.Select(v => (AreaType)v).ToList();
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(sequence);
            await _cacheHelper.HashSetAsync(JobPoolRedisKey, ForcedSequenceRedisField, json);
        }

        _logger.LogInformation(
            "폐쇄 config 변경 — startDelaySec={StartDelay}, intervalSec={Interval}, forcedSequence={Seq}",
            _startDelaySec, _intervalSec,
            _forcedSequence != null ? string.Join(",", _forcedSequence) : "무작위");
    }

    public async Task ResetClosureConfigAsync()
    {
        _startDelaySec = DemoMode.IsActive ? DemoMode.ClosureStartDelaySec : DefaultStartDelaySec;
        _intervalSec = DemoMode.IsActive ? DemoMode.ClosureIntervalSec : DefaultIntervalSec;
        // DEMO_MODE 활성 시 DemoMode.ForcedClosureSequence로 복원 (GDD 정합), 비활성 시 무작위(null)
        _forcedSequence = DemoMode.IsActive ? new List<AreaType>(DemoMode.ForcedClosureSequence) : null;
        await _cacheHelper.HashDeleteAsync(JobPoolRedisKey, StartDelayRedisField);
        await _cacheHelper.HashDeleteAsync(JobPoolRedisKey, IntervalRedisField);
        await _cacheHelper.HashDeleteAsync(JobPoolRedisKey, ForcedSequenceRedisField);
        _logger.LogInformation("폐쇄 config 초기화 (DEMO_MODE={Demo}, 시퀀스={Seq})",
            DemoMode.IsActive, _forcedSequence != null ? "DemoMode 시퀀스" : "무작위");
    }

    public async Task ClearForcedSequenceAsync()
    {
        // DEMO_MODE 활성 시 DemoMode.ForcedClosureSequence로 복원, 비활성 시 무작위(null)
        _forcedSequence = DemoMode.IsActive ? new List<AreaType>(DemoMode.ForcedClosureSequence) : null;
        await _cacheHelper.HashDeleteAsync(JobPoolRedisKey, ForcedSequenceRedisField);
        _logger.LogInformation("폐쇄 시퀀스 강제 지정 해제 (DEMO_MODE={Demo}, 시퀀스={Seq})",
            DemoMode.IsActive, _forcedSequence != null ? "DemoMode 시퀀스" : "무작위");
    }

    // ─── 직책 풀 Config (Redis 공유) ─────────────────────────────────────────

    /// <summary>
    ///     Redis에서 직책 풀 config 읽기.
    ///     키가 없으면 null (무작위) 반환.
    /// </summary>
    public async Task<List<JobTitle>?> GetJobPoolConfigAsync()
    {
        try
        {
            var raw = await _cacheHelper.HashGetAsync(JobPoolRedisKey, JobPoolRedisField);
            if (raw.HasValue)
            {
                var ints = JsonSerializer.Deserialize<List<int>>((string)raw!);
                return ints?.Select(v => (JobTitle)v).ToList();
            }

            // DEMO_MODE 활성 + Redis에 강제 직책 풀 없으면 DemoMode.ChainJobOrder 기본 적용
            if (DemoMode.IsActive)
                return new List<JobTitle>(DemoMode.ChainJobOrder);

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "직책 풀 config 읽기 실패");
            return null;
        }
    }

    /// <summary>
    ///     직책 풀 config 저장. jobs=null이면 키 삭제(무작위 복원).
    /// </summary>
    public async Task SetJobPoolConfigAsync(List<int>? jobs)
    {
        try
        {
            if (jobs == null)
            {
                await _cacheHelper.HashDeleteAsync(JobPoolRedisKey, JobPoolRedisField);
                _logger.LogInformation("직책 풀 config 삭제 (무작위 복원)");
                return;
            }

            var json = JsonSerializer.SerializeToUtf8Bytes(jobs);
            await _cacheHelper.HashSetAsync(JobPoolRedisKey, JobPoolRedisField, json);
            _logger.LogInformation("직책 풀 config 저장 — jobs={Jobs}",
                string.Join(",", jobs.Select(j => ((JobTitle)j).ToString())));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "직책 풀 config 저장 실패");
            throw;
        }
    }

    /// <summary>
    ///     직책 풀 config 초기화. DEMO_MODE 활성 시 ChainJobOrder로 복원, 비활성 시 무작위.
    /// </summary>
    public async Task ResetJobPoolConfigAsync()
    {
        if (DemoMode.IsActive)
        {
            // user_server가 Redis 직접 읽으므로 DemoMode.ChainJobOrder를 명시적으로 Redis에 쓰기
            await SetJobPoolConfigAsync(DemoMode.ChainJobOrder.Select(j => (int)j).ToList());
            _logger.LogInformation("직책 풀 config 초기화 — DEMO_MODE 활성 → ChainJobOrder 적용");
        }
        else
        {
            await SetJobPoolConfigAsync(null);
        }
    }

    // ─── 현재 전체 config 조회 (GET endpoint용) ───────────────────────────────

    public async Task<MatchingConfigSnapshot> GetSnapshotAsync()
    {
        var closure = GetClosureConfig();
        var jobPool = await GetJobPoolConfigAsync();

        return new MatchingConfigSnapshot
        {
            StartDelaySec = closure.StartDelaySec,
            IntervalSec = closure.IntervalSec,
            ForcedSequence = closure.ForcedSequence?.Select(a => (int)a).ToList(),
            ForcedJobs = jobPool?.Select(j => (int)j).ToList()
        };
    }
}

/// <summary>폐쇄 config 값 객체</summary>
public class ClosureConfig
{
    public int StartDelaySec { get; set; }
    public int IntervalSec { get; set; }
    public List<AreaType>? ForcedSequence { get; set; }
}

/// <summary>GET /admin/matching-config 응답 DTO</summary>
public class MatchingConfigSnapshot
{
    /// <summary>첫 폐쇄까지 딜레이 (초)</summary>
    public int StartDelaySec { get; set; }

    /// <summary>구역 간 폐쇄 간격 (초)</summary>
    public int IntervalSec { get; set; }

    /// <summary>강제 폐쇄 시퀀스 (null=무작위)</summary>
    public List<int>? ForcedSequence { get; set; }

    /// <summary>강제 직책 풀 (null=무작위 8개 중 5/8)</summary>
    public List<int>? ForcedJobs { get; set; }
}

/// <summary>POST /admin/matching-config/closure 요청 DTO</summary>
public class SetClosureConfigRequest
{
    /// <summary>폐쇄 시작 딜레이 (null=변경 안함)</summary>
    public int? StartDelaySec { get; set; }

    /// <summary>폐쇄 간격 (null=변경 안함)</summary>
    public int? IntervalSec { get; set; }

    /// <summary>강제 폐쇄 시퀀스 (null=무작위 복원, 빈 목록=무작위)</summary>
    public List<int>? Sequence { get; set; }

    /// <summary>true이면 시퀀스를 무작위로 초기화</summary>
    public bool ResetSequence { get; set; }

    /// <summary>true이면 폐쇄 config 전체를 기본값으로 초기화</summary>
    public bool ResetAll { get; set; }
}

/// <summary>POST /admin/matching-config/job-pool 요청 DTO</summary>
public class SetJobPoolConfigRequest
{
    /// <summary>강제 직책 풀 (null=무작위 복원)</summary>
    public List<int>? Jobs { get; set; }
}
