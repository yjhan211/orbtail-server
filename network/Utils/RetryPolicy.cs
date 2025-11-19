using Microsoft.Extensions.Logging;

namespace network.utils;

/// <summary>
/// 간단한 재시도 정책 유틸리티
/// 실패한 작업을 지수 백오프로 재시도
/// </summary>
public static class RetryPolicy
{
    /// <summary>
    /// 지수 백오프로 작업 재시도
    /// </summary>
    /// <param name="operation">실행할 작업</param>
    /// <param name="maxRetries">최대 재시도 횟수 (기본: 3)</param>
    /// <param name="baseDelayMs">기본 지연 시간(ms) (기본: 100)</param>
    /// <param name="logger">로거 (선택)</param>
    /// <param name="operationName">작업 이름 (로깅용)</param>
    /// <typeparam name="T">반환 타입</typeparam>
    /// <returns>작업 결과</returns>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        int maxRetries = 3,
        int baseDelayMs = 100,
        ILogger? logger = null,
        string operationName = "Operation")
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                attempt++;
                var delay = baseDelayMs * (int)Math.Pow(2, attempt - 1);

                logger?.LogWarning(ex,
                    "{OperationName} failed (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}ms...",
                    operationName, attempt, maxRetries, delay);

                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex,
                    "{OperationName} failed after {MaxRetries} attempts",
                    operationName, maxRetries);
                throw;
            }
        }
    }

    /// <summary>
    /// 지수 백오프로 작업 재시도 (반환값 없음)
    /// </summary>
    public static async Task ExecuteWithRetryAsync(
        Func<Task> operation,
        int maxRetries = 3,
        int baseDelayMs = 100,
        ILogger? logger = null,
        string operationName = "Operation")
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await operation();
            return Task.CompletedTask;
        }, maxRetries, baseDelayMs, logger, operationName);
    }

    /// <summary>
    /// 특정 예외 타입에만 재시도
    /// </summary>
    public static async Task<T> ExecuteWithRetryAsync<T, TException>(
        Func<Task<T>> operation,
        int maxRetries = 3,
        int baseDelayMs = 100,
        ILogger? logger = null,
        string operationName = "Operation")
        where TException : Exception
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await operation();
            }
            catch (TException ex) when (attempt < maxRetries)
            {
                attempt++;
                var delay = baseDelayMs * (int)Math.Pow(2, attempt - 1);

                logger?.LogWarning(ex,
                    "{OperationName} failed with {ExceptionType} (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}ms...",
                    operationName, typeof(TException).Name, attempt, maxRetries, delay);

                await Task.Delay(delay);
            }
        }
    }
}
