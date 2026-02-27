using PrintNest.Application.Interfaces;

namespace PrintNest.IntegrationTests.Infrastructure;

internal sealed class DeterministicReleaseConcurrencyHook : IReleaseConcurrencyTestHook, IDisposable
{
    private readonly Barrier _barrier = new(2);
    private Guid _targetJobId;
    private int _participantCount;

    public int ParticipantCount => Volatile.Read(ref _participantCount);

    public void SetTargetJob(Guid jobId) => _targetJobId = jobId;

    public Task BeforeSaveAsync(Guid jobId, string deviceId, CancellationToken cancellationToken = default)
    {
        if (jobId != _targetJobId)
            return Task.CompletedTask;

        Interlocked.Increment(ref _participantCount);

        var bothArrived = _barrier.SignalAndWait(millisecondsTimeout: 5000, cancellationToken);
        if (!bothArrived)
            throw new TimeoutException($"Timed out waiting for concurrent release request for job '{jobId}'.");

        return Task.CompletedTask;
    }

    public void Dispose() => _barrier.Dispose();
}
