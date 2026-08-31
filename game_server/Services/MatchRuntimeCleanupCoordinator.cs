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
    ///     Attempts the Active-to-Finalizing transition and runs the cleanup plan exactly once.
    ///     The optional predicate and pre-cleanup action execute under the runtime lifecycle boundary.
    /// </summary>
    public bool TryFinalize(
        long matchingId,
        Func<bool>? canFinalize = null,
        Action? beforeCleanup = null)
    {
        try
        {
            return runtimeRegistry.TryFinalize(matchingId, canFinalize ?? (static () => true), () =>
            {
                if (beforeCleanup != null)
                    RunStep(matchingId, "match finalization", beforeCleanup);

                foreach (MatchRuntimeCleanupStep step in cleanupSteps)
                    RunStep(matchingId, step.Name, () => step.Cleanup(matchingId));
            });
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
