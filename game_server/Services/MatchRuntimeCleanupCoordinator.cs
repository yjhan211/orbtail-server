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
    ///     Attempts to register the ordered cleanup plan and guarded post-commit continuation.
    /// </summary>
    /// <returns>
    ///     <list type="table">
    ///         <listheader>
    ///             <term>Result and state</term>
    ///             <description>Contract</description>
    ///         </listheader>
    ///         <item>
    ///             <term>true / Active winner</term>
    ///             <description>The predicate and before-cleanup action run under the lifecycle boundary,
    ///             cleanup runs exactly once, and post runs after commit outside it.</description>
    ///         </item>
    ///         <item>
    ///             <term>false / Active rejected or invalid id</term>
    ///             <description>No cleanup or post is registered.</description>
    ///         </item>
    ///         <item>
    ///             <term>false / Finalizing</term>
    ///             <description>Before-cleanup and component cleanup are not repeated; a non-null guarded
    ///             post is attached to the pending finalization.</description>
    ///         </item>
    ///         <item>
    ///             <term>false / Completed or tombstoned</term>
    ///             <description>Before-cleanup and component cleanup are not repeated; a non-null guarded
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
        Action? beforeCleanup = null,
        Action? afterFinalized = null)
    {
        try
        {
            return runtimeRegistry.TryFinalize(matchingId, canFinalize ?? (static () => true), () =>
            {
                if (beforeCleanup != null)
                    RunStep(matchingId, "match finalization", beforeCleanup);

                foreach (MatchRuntimeCleanupStep step in cleanupSteps)
                    RunStep(matchingId, step.Name, () => step.Cleanup(matchingId));
            }, afterFinalized == null
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
