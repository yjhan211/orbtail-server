namespace user_server.application.queries;

/// <summary>
/// Base interface for query handlers
/// </summary>
/// <typeparam name="TQuery">The query type to handle</typeparam>
/// <typeparam name="TResult">The result type returned by the query</typeparam>
public interface IQueryHandler<in TQuery, TResult>
{
    /// <summary>
    /// Handles the query asynchronously
    /// </summary>
    /// <param name="query">The query to handle</param>
    /// <returns>Task containing the query result</returns>
    Task<TResult> HandleAsync(TQuery query);
}
