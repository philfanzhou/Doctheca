namespace Doctheca.Host.Health;

/// <summary>
/// The bounded, process-local receipt of this host instance's own startup initialization.
/// </summary>
/// <remarks>
/// Doctheca has no installation database, so there is nothing durable to read back: the only
/// authoritative fact a readiness probe may rely on before touching PostgreSQL is whether
/// <c>DocthecaMigrationExecutor.ExecuteAsync</c> returned successfully in this process. The
/// receipt starts as "not completed" and can only move forward, exactly once, when the executor
/// returns. It never fabricates an installation record and never borrows the ServiceMantle
/// Bootstrap phase resolver. A successful executor return still is not re-read from the
/// database, which is why the snapshot source also runs its read-only schema probe before
/// publishing readiness.
/// </remarks>
public sealed class DocthecaStartupReceipt
{
    private const int Running = 0;
    private const int Completed = 1;

    private int _state = Running;

    /// <summary>Gets whether this process completed its startup initialization.</summary>
    public bool InitializationCompleted => Volatile.Read(ref _state) == Completed;

    /// <summary>
    /// Records the one-way transition to "initialization completed". Calling it again is a
    /// no-op; there is no path back to the running state within a process lifetime.
    /// </summary>
    public void MarkInitializationCompleted() =>
        Interlocked.Exchange(ref _state, Completed);
}
