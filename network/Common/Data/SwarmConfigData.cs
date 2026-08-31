// ReSharper disable All

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using network.common.data.helpers;

namespace network.common.data
{
    /// <summary>
    ///     스웜 밸런스 key/value 스토어 (swarm_config.csv, #296).
    ///     Config의 스칼라 프로퍼티가 여기서 읽고, 미로드·미등재 키는 코드 기본값으로 폴백한다 —
    ///     CSV 없이도(테스트·부트스트랩 초기) 현행 값으로 동작한다.
    ///     배열 값은 "0.7|0.85|1" 처럼 파이프 구분으로 저작한다.
    /// </summary>
    public static class SwarmConfigData
    {
        private static readonly Dictionary<string, string> _raw = new();
        private static readonly ConcurrentDictionary<string, float> _floatCache = new();
        private static readonly ConcurrentDictionary<string, int> _intCache = new();
        private static readonly ConcurrentDictionary<string, double> _doubleCache = new();
        private static readonly ConcurrentDictionary<string, float[]> _floatArrayCache = new();

        public static void Initialize(List<CsvRow> rows)
        {
            _raw.Clear();
            _floatCache.Clear();
            _intCache.Clear();
            _doubleCache.Clear();
            _floatArrayCache.Clear();

            foreach (var row in rows)
            {
                string key = row["id"]?.Trim() ?? "";
                if (string.IsNullOrEmpty(key))
                    continue;
                if (!_raw.TryAdd(key, row["value"]?.Trim() ?? ""))
                    throw new ArgumentException($"Duplicate swarm config key: {key}");
            }
        }

        public static float GetFloat(string key, float fallback) =>
            _raw.TryGetValue(key, out var value)
                ? _floatCache.GetOrAdd(key, _ => float.Parse(value, CultureInfo.InvariantCulture))
                : fallback;

        public static int GetInt(string key, int fallback) =>
            _raw.TryGetValue(key, out var value)
                ? _intCache.GetOrAdd(key, _ => int.Parse(value, CultureInfo.InvariantCulture))
                : fallback;

        public static double GetDouble(string key, double fallback) =>
            _raw.TryGetValue(key, out var value)
                ? _doubleCache.GetOrAdd(key, _ => double.Parse(value, CultureInfo.InvariantCulture))
                : fallback;

        public static float[] GetFloatArray(string key, float[] fallback) =>
            _raw.TryGetValue(key, out var value)
                ? _floatArrayCache.GetOrAdd(key, _ => value
                    .Split('|')
                    .Select(part => float.Parse(part.Trim(), CultureInfo.InvariantCulture))
                    .ToArray())
                : fallback;
    }
}
