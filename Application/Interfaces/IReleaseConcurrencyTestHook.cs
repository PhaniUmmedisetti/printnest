namespace PrintNest.Application.Interfaces;

/// <summary>
/// Internal test hook for forcing deterministic concurrency timing in release flow.
/// Implementations should be registered only in integration tests.
/// </summary>
internal interface IReleaseConcurrencyTestHook
{
    Task BeforeSaveAsync(Guid jobId, string deviceId, CancellationToken cancellationToken = default);
}
