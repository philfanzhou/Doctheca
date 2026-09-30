using ServiceMantle.Health;

namespace Doctheca.Host.Health;

/// <summary>
/// The bounded, process-local observation of this host instance's own startup initialization
/// (issue #53): <see cref="ServiceMigrationReadinessState.Running"/> until the startup gate
/// decides, then exactly one terminal transition to <see cref="ServiceMigrationReadinessState.Succeeded"/>
/// or <see cref="ServiceMigrationReadinessState.Failed"/>.
/// </summary>
/// <remarks>
/// Doctheca has no installation database, so there is nothing durable to read back: the only
/// authoritative fact a readiness probe may rely on before touching PostgreSQL is the outcome of
/// this process's own startup sequence (deployment validation, target preparation, and the
/// ServiceMantle migration orchestration). <see cref="MarkSucceeded"/> is recorded only after
/// the orchestrator returned <c>Succeeded</c>; any phase failure records
/// <see cref="MarkFailed"/> before the exception escapes and stops the host with a non-zero
/// exit code. Terminal states are final — there is no path back to Running within a process
/// lifetime, and a failed production host never listens (the Failed state exists so injected
/// scenarios observe the honest value). The receipt never fabricates an installation record and
/// never borrows the ServiceMantle Bootstrap phase resolver; the snapshot source still runs its
/// read-only schema probe before publishing readiness.
/// </remarks>
public sealed class DocthecaStartupReceipt
{
    private int _state = (int)ServiceMigrationReadinessState.Running;

    /// <summary>Gets the current startup-observation state without any I/O.</summary>
    public ServiceMigrationReadinessState StartupState =>
        (ServiceMigrationReadinessState)Volatile.Read(ref _state);

    /// <summary>
    /// Gets whether this process completed its startup initialization successfully. This is the
    /// only condition under which the health snapshot may report
    /// <see cref="ServiceMigrationReadinessState.Succeeded"/>.
    /// </summary>
    public bool InitializationSucceeded =>
        StartupState == ServiceMigrationReadinessState.Succeeded;

    /// <summary>
    /// Records the one-way transition to "initialization succeeded". Calling it again is a
    /// no-op, and a terminal state never transitions again.
    /// </summary>
    public void MarkSucceeded() =>
        TransitionTo(ServiceMigrationReadinessState.Succeeded);

    /// <summary>
    /// Records a startup failure (any phase: deployment validation, target preparation, or
    /// migration orchestration). Terminal states are final.
    /// </summary>
    public void MarkFailed() =>
        TransitionTo(ServiceMigrationReadinessState.Failed);

    private void TransitionTo(ServiceMigrationReadinessState target)
    {
        var current = Volatile.Read(ref _state);
        while (true)
        {
            var currentIsTerminal =
                current == (int)ServiceMigrationReadinessState.Succeeded ||
                current == (int)ServiceMigrationReadinessState.Failed;
            if (currentIsTerminal)
            {
                return;
            }

            var previous = Interlocked.CompareExchange(ref _state, (int)target, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }
}
