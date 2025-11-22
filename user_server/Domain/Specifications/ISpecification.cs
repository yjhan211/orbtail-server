namespace user_server.domain.specifications;

/// <summary>
/// Base interface for specifications
/// Used to encapsulate business rules that can be reused and combined
/// </summary>
/// <typeparam name="T">The type being evaluated</typeparam>
public interface ISpecification<T>
{
    /// <summary>
    /// Checks if the given entity satisfies this specification
    /// </summary>
    /// <param name="entity">The entity to check</param>
    /// <returns>True if satisfied, false otherwise</returns>
    bool IsSatisfiedBy(T entity);

    /// <summary>
    /// Combines this specification with another using AND logic
    /// </summary>
    ISpecification<T> And(ISpecification<T> other);

    /// <summary>
    /// Combines this specification with another using OR logic
    /// </summary>
    ISpecification<T> Or(ISpecification<T> other);

    /// <summary>
    /// Negates this specification
    /// </summary>
    ISpecification<T> Not();
}
