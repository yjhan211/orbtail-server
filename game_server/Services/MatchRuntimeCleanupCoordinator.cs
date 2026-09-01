using Microsoft.Extensions.Logging;

namespace game_server.services;

/// <summary>
///     Describes one ordered, match-scoped cleanup action.
/// </summary>
internal sealed record MatchRuntimeCleanupStep(string Name, Action<long> Cleanup);

/// <summary>
///     Owns the terminal transition and ordered cleanup plan for an in-memory match runtime.
///     Each cleanup step is isolated so a component failure cannot prevent later components from releasing state.
/// </summary>
internal sealed class MatchRuntimeCleanupCoordinator(
    MatchRuntimeRegistry runtimeRegistry,
    IReadOnlyList<MatchRuntimeCleanupStep> cleanupSteps,
    ILogger logger)
{
    /// <summary>
    ///     Attempts to register guarded pre-cleanup/post-commit hooks around the ordered,
    ///     winner-owned component cleanup plan.
    /// </summary>
    /// <returns>
    ///     <list type="table">
    ///         <listheader>
    ///             <term>Result and state</term>
    ///             <description>Contract</description>
    ///         </listheader>
    ///         <item>
    ///             <term>true / Active winner</term>
    ///             <description>The predicate and before-finalized hook run under the lifecycle boundary,
    ///             component cleanup runs exactly once, and post runs after commit outside it.</description>
    ///         </item>
    ///         <item>
    ///             <term>false / Active rejected or invalid id</term>
    ///             <description>No cleanup or post is registered.</description>
    ///         </item>
    ///         <item>
    ///             <term>false / Finalizing</term>
    ///             <description>Component cleanup is not repeated. Non-null guarded before/post hooks are
    ///             attached to pending work in registration order.</description>
    ///         </item>
    ///         <item>
    ///             <term>false / Completed or tombstoned</term>
    ///             <description>Before-finalized and component cleanup are not repeated. A non-null guarded
    ///             post executes immediately outside the monitor.</description>
    ///         </item>
    ///         <item>
    ///             <term>false / exception</term>
    ///             <description>The exception is logged; registry cleanup/post state follows the transition
    ///             outcome described above.</description>
    ///         </item>
    ///     </list>
    /// </returns>
    public bool TryFinalize(
        long matchingId,
        Func<bool>? canFinalize = null,
        Action? beforeFinalized = null,
        Action? afterFinalized = null)
    {
        try
        {
            return runtimeRegistry.TryFinalize(
                matchingId,
                canFinalize ?? (static () => true),
                beforeFinalized == null
                    ? null
                    : () => RunStep(matchingId, "match pre-finalization", beforeFinalized),
                () =>
                {
                    foreach (MatchRuntimeCleanupStep step in cleanupSteps)
                        RunStep(matchingId, step.Name, () => step.Cleanup(matchingId));
                },
                afterFinalized == null
                ? null
                : () => RunStep(matchingId, "match post-finalization", afterFinalized));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Match runtime cleanup failed: MatchingId={MatchingId}", matchingId);
            return false;
        }
    }

    private void RunStep(long matchingId, string component, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Match component cleanup failed: MatchingId={MatchingId}, Component={Component}",
                matchingId,
                component);
        }
    }
}
