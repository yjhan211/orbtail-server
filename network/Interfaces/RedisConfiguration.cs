using StackExchange.Redis;

namespace network.interfaces;

/// <summary>
///     검증이 끝난 Redis 클라이언트 구성이다.
///     ConnectionString에는 인증 정보가 포함될 수 있으므로 로그에 남기지 않는다.
/// </summary>
public sealed class RedisConfiguration
{
    private readonly string _connectionString;

    internal RedisConfiguration(ConfigurationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _connectionString = options.ToString(true);
        Endpoints = options.EndPoints.Select(endpoint => endpoint.ToString() ?? string.Empty).ToArray();
        HasPassword = !string.IsNullOrEmpty(options.Password);
        TlsEnabled = options.Ssl;
        SslHost = options.SslHost;
        ConnectTimeoutMilliseconds = options.ConnectTimeout;
        SyncTimeoutMilliseconds = options.SyncTimeout;
        AsyncTimeoutMilliseconds = options.AsyncTimeout;
        ConnectRetry = options.ConnectRetry;
        AbortOnConnectFail = options.AbortOnConnectFail;
    }

    public IReadOnlyList<string> Endpoints { get; }
    public bool HasPassword { get; }
    public bool TlsEnabled { get; }
    public string? SslHost { get; }
    public int ConnectTimeoutMilliseconds { get; }
    public int SyncTimeoutMilliseconds { get; }
    public int AsyncTimeoutMilliseconds { get; }
    public int ConnectRetry { get; }
    public bool AbortOnConnectFail { get; }

    public string ConnectionString => _connectionString;

    internal ConfigurationOptions CreateClientOptions()
    {
        return ConfigurationOptions.Parse(_connectionString);
    }

    public override string ToString()
    {
        return string.Join(',', Endpoints);
    }
}
