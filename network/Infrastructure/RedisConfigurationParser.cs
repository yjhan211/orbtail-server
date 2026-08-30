using System.Globalization;
using Microsoft.Extensions.Configuration;
using network.interfaces;
using StackExchange.Redis;

namespace network.infrastructure;

public static class RedisConfigurationParser
{
    public static RedisConfiguration Parse(
        IConfiguration configuration,
        string defaultEndpoints = "localhost:6379"
    )
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string connectionString = FirstConfiguredValue(
                                      configuration,
                                      "Redis:ConnectionString",
                                      "redisConnectionString"
                                  ) ??
                                  FirstConfiguredValue(
                                      configuration,
                                      "Redis:Endpoints",
                                      "redisEndpoints"
                                  ) ??
                                  defaultEndpoints;

        ConfigurationOptions options = ParseOptions(connectionString);

        string? password = FirstPresentValue(configuration, "Redis:Password", "redisPassword");
        if (password != null) options.Password = string.IsNullOrEmpty(password) ? null : password;

        bool? tlsEnabled = ParseOptionalBoolean(
            configuration,
            "Redis:TlsEnabled",
            "redisTlsEnabled",
            "redisSsl"
        );
        if (tlsEnabled.HasValue) options.Ssl = tlsEnabled.Value;

        string? sslHost = FirstPresentValue(configuration, "Redis:SslHost", "redisSslHost");
        if (sslHost != null) options.SslHost = string.IsNullOrWhiteSpace(sslHost) ? null : sslHost.Trim();

        options.ConnectTimeout = ParseOptionalPositiveInteger(
                                     configuration,
                                     "Redis:ConnectTimeoutMilliseconds",
                                     "redisConnectTimeoutMilliseconds",
                                     "redisConnectTimeoutMs"
                                 ) ??
                                 options.ConnectTimeout;
        options.SyncTimeout = ParseOptionalPositiveInteger(
                                  configuration,
                                  "Redis:SyncTimeoutMilliseconds",
                                  "redisSyncTimeoutMilliseconds",
                                  "redisSyncTimeoutMs"
                              ) ??
                              options.SyncTimeout;
        options.AsyncTimeout = ParseOptionalPositiveInteger(
                                   configuration,
                                   "Redis:AsyncTimeoutMilliseconds",
                                   "redisAsyncTimeoutMilliseconds",
                                   "redisAsyncTimeoutMs"
                               ) ??
                               options.AsyncTimeout;
        options.ConnectRetry = ParseOptionalNonNegativeInteger(
                                   configuration,
                                   "Redis:ConnectRetry",
                                   "redisConnectRetry"
                               ) ??
                               options.ConnectRetry;
        options.AbortOnConnectFail = ParseOptionalBoolean(
                                         configuration,
                                         "Redis:AbortOnConnectFail",
                                         "redisAbortOnConnectFail"
                                     ) ??
                                     false;

        Validate(options);
        return new RedisConfiguration(options);
    }

    public static RedisConfiguration ParseConnectionString(string connectionString)
    {
        ConfigurationOptions options = ParseOptions(connectionString);
        options.AbortOnConnectFail = false;
        Validate(options);
        return new RedisConfiguration(options);
    }

    private static ConfigurationOptions ParseOptions(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Redis endpoints are not configured.");
        }

        try
        {
            return ConfigurationOptions.Parse(connectionString.Trim());
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("Redis configuration is invalid.", ex);
        }
    }

    private static void Validate(ConfigurationOptions options)
    {
        if (options.EndPoints.Count == 0)
        {
            throw new InvalidOperationException("At least one Redis endpoint must be configured.");
        }

        if (options.ConnectTimeout <= 0)
        {
            throw new InvalidOperationException("Redis connect timeout must be greater than zero.");
        }

        if (options.SyncTimeout <= 0)
        {
            throw new InvalidOperationException("Redis sync timeout must be greater than zero.");
        }

        if (options.AsyncTimeout <= 0)
        {
            throw new InvalidOperationException("Redis async timeout must be greater than zero.");
        }

        if (options.ConnectRetry < 0)
        {
            throw new InvalidOperationException("Redis connect retry count cannot be negative.");
        }
    }

    private static string? FirstConfiguredValue(IConfiguration configuration, params string[] keys)
    {
        return keys.Select(key => configuration[key]).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? FirstPresentValue(IConfiguration configuration, params string[] keys)
    {
        foreach (string key in keys)
        {
            string? value = configuration[key];
            if (value != null) return value;
        }

        return null;
    }

    private static bool? ParseOptionalBoolean(IConfiguration configuration, params string[] keys)
    {
        string? value = FirstPresentValue(configuration, keys);
        if (value == null) return null;
        if (bool.TryParse(value, out bool parsed)) return parsed;
        if (value == "1") return true;
        if (value == "0") return false;

        throw new InvalidOperationException($"Redis option '{keys[0]}' must be a boolean value.");
    }

    private static int? ParseOptionalPositiveInteger(IConfiguration configuration, params string[] keys)
    {
        int? value = ParseOptionalInteger(configuration, keys);
        if (!value.HasValue) return null;
        if (value.Value > 0) return value;

        throw new InvalidOperationException($"Redis option '{keys[0]}' must be greater than zero.");
    }

    private static int? ParseOptionalNonNegativeInteger(IConfiguration configuration, params string[] keys)
    {
        int? value = ParseOptionalInteger(configuration, keys);
        if (!value.HasValue) return null;
        if (value.Value >= 0) return value;

        throw new InvalidOperationException($"Redis option '{keys[0]}' cannot be negative.");
    }

    private static int? ParseOptionalInteger(IConfiguration configuration, params string[] keys)
    {
        string? value = FirstPresentValue(configuration, keys);
        if (value == null) return null;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) return parsed;

        throw new InvalidOperationException($"Redis option '{keys[0]}' must be an integer value.");
    }
}
