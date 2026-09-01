using Microsoft.Extensions.Logging;

namespace game_server.services;

/// <summary>
///     Describes one ordered, match-scoped cleanup action.
/// </summary>
internal sealed record MatchRuntimeCleanupStep(string Name, Action<long> Cleanup);

/// <summary>
///     Owns the terminal transition and ordered cleanup plan for an in-memory match runtime.
///     Pre-finalization hooks run after the depth-zero claim outside the runtime monitor, component
///     cleanup runs under it, and winner/caller post-commit work runs outside it. Each step is isolated
///     so one failure cannot prevent later components from releasing state.
/// </summary>
internal sealed class MatchRuntimeCleanupCoordinator(
    MatchRuntimeRegistry runtimeRegistry,
    IReadOnlyList<MatchRuntimeCleanupStep> cleanupSteps,
    ILogger logger,
    Func<long, Action?>? prepareWinnerPostCommit = null)
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
    ///             <description>The predicate and before snapshot are guarded by the lifecycle boundary.
    ///             Before-finalized hooks run outside the monitor, component cleanup runs exactly once
    ///             under it, and post runs after commit outside it.</description>
    ///         </item>
    ///         <item>
    ///             <term>false / Active rejected or invalid id</term>
    ///             <description>No cleanup or post is registered.</description>
    ///         </item>
    ///         <item>
    ///             <term>false / Finalizing</term>
    ///             <description>Component cleanup is not repeated. A non-null before hook attaches only
    ///             while the before snapshot is still pending; a post hook attaches until commit.</description>
    ///         </item>
    ///         <item>
    ///             <term>false / Completed or tombstoned</term>
    ///             <description>Before-finalized and component cleanup are not repeated. A non-null guarded
    ///             post executes immediately outside the monitor.</description>
    ///         </item>
    ///         <item>
    ///             <term>false / exception</term>
    ///             <description>RunStep logs and isolates individual before, component, winner-post, and
    ///             caller-post failures so later stages and terminal commit continue. Only registry/lifecycle
    ///             orchestration failure reaches the outer catch and is returned as false. Registry-level
    ///             cleanup failure abandons the completed before snapshot; a later finalization attempt may
    ///             register a new hook but never replays the abandoned work's frozen callbacks.</description>
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
            Action? winnerPostCommit = null;
            Action? postCommit =
                prepareWinnerPostCommit == null && afterFinalized == null
                    ? null
                    : () =>
                    {
                        if (winnerPostCommit != null)
                        {
                            RunStep(
                                matchingId,
                                "match winner post-finalization",
                                winnerPostCommit);
                        }

                        if (afterFinalized != null)
                        {
                            RunStep(
                                matchingId,
                                "match post-finalization",
                                afterFinalized);
                        }
                    };

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

                    if (prepareWinnerPostCommit != null)
                    {
                        RunStep(
                            matchingId,
                            "match winner post-finalization registration",
                            () => winnerPostCommit = prepareWinnerPostCommit(matchingId));
                    }
                },
                postCommit);
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
