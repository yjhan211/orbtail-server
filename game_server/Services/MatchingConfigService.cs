using System.Text.Json;
using Microsoft.Extensions.Logging;
using network.common;
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
            if (startRaw.HasValue && int.TryParse((string)startRaw!, out int startDelay))
                _startDelaySec = Math.Max(0, startDelay);

            var intervalRaw = await _cacheHelper.HashGetAsync(JobPoolRedisKey, IntervalRedisField);
            if (intervalRaw.HasValue && int.TryParse((string)intervalRaw!, out int interval))
                _intervalSec = Math.Max(1, interval);

            var sequenceRaw = await _cacheHelper.HashGetAsync(JobPoolRedisKey, ForcedSequenceRedisField);
            if (sequenceRaw.HasValue)
            {
                var values = JsonSerializer.Deserialize<List<int>>((string)sequenceRaw!);
                _forcedSequence = values?.Select(value => (AreaType)value).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load closure config from Redis.");
        }
    }

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
        _startDelaySec = DefaultStartDelaySec;
        _intervalSec = DefaultIntervalSec;
        _forcedSequence = null;
        await _cacheHelper.HashDeleteAsync(JobPoolRedisKey, StartDelayRedisField);
        await _cacheHelper.HashDeleteAsync(JobPoolRedisKey, IntervalRedisField);
        await _cacheHelper.HashDeleteAsync(JobPoolRedisKey, ForcedSequenceRedisField);
        _logger.LogInformation("Closure config reset to defaults.");
    }

    public async Task ClearForcedSequenceAsync()
    {
        _forcedSequence = null;
        await _cacheHelper.HashDeleteAsync(JobPoolRedisKey, ForcedSequenceRedisField);
        _logger.LogInformation("Forced closure sequence cleared.");
    }

    // ─── 현재 전체 config 조회 (GET endpoint용) ───────────────────────────────

    public Task<MatchingConfigSnapshot> GetSnapshotAsync()
    {
        var closure = GetClosureConfig();

        return Task.FromResult(new MatchingConfigSnapshot
        {
            StartDelaySec = closure.StartDelaySec,
            IntervalSec = closure.IntervalSec,
            ForcedSequence = closure.ForcedSequence?.Select(a => (int)a).ToList()
        });
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
