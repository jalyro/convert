using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jalyro.Convert.Host;

/// <summary>
/// Serialises jobs so a user who right-clicks five times in a row gets five
/// jobs in order rather than five competing bursts of workers.
///
/// Files WITHIN a job still run concurrently, capped by ConversionService.
/// That is the right level for the cap: parallelism across jobs would multiply
/// the cap by the number of queued jobs.
/// </summary>
internal sealed class JobQueue : IDisposable
{
    public sealed class QueuedJob
    {
        public required JobManifest Manifest { get; init; }
        public CancellationTokenSource Cancellation { get; } = new();
        public int Total => Manifest.Paths.Count;
        public int Completed;
        public int Failed;
    }

    /// <summary>Fired on the UI thread. Called for queue, start, per-file, done.</summary>
    public event Action<QueuedJob>? Changed;
    public event Action<QueuedJob, ConversionService.JobOutcome>? Finished;

    /// <summary>Progress within the file currently converting, 0-100.</summary>
    public event Action<int>? FileProgress;

    private readonly BlockingCollection<QueuedJob> _pending = new();
    private readonly ConversionService _converter;
    private readonly Task _pump;
    private readonly CancellationTokenSource _shutdown = new();

    private QueuedJob? _current;
    public QueuedJob? Current => _current;
    public int PendingCount => _pending.Count;

    public JobQueue(ConversionService converter)
    {
        _converter = converter;
        _converter.FileProgressChanged += p => FileProgress?.Invoke(p.Percent);
        _pump = Task.Run(PumpAsync);
    }

    public QueuedJob Enqueue(JobManifest manifest)
    {
        var job = new QueuedJob { Manifest = manifest };
        _pending.Add(job);
        Changed?.Invoke(job);
        return job;
    }

    /// <summary>Cancels the running job. Queued jobs are untouched.</summary>
    public void CancelCurrent()
    {
        try { _current?.Cancellation.Cancel(); }
        catch { /* already gone */ }
    }

    /// <summary>Cancels everything, including whatever is queued behind.</summary>
    public void CancelAll()
    {
        CancelCurrent();
        while (_pending.TryTake(out QueuedJob? queued))
        {
            try { queued.Cancellation.Cancel(); } catch { }
        }
    }

    private static List<ConversionService.FileOutcome> OutcomeForAll(
        QueuedJob job, string reason)
    {
        var list = new List<ConversionService.FileOutcome>(job.Manifest.Paths.Count);
        foreach (string p in job.Manifest.Paths)
            list.Add(new ConversionService.FileOutcome(p, null, reason));
        return list;
    }

    private async Task PumpAsync()
    {
        try
        {
            foreach (QueuedJob job in _pending.GetConsumingEnumerable(_shutdown.Token))
            {
                _current = job;
                Changed?.Invoke(job);

                ConversionService.JobOutcome outcome;
                try
                {
                    outcome = await _converter.RunAsync(
                        job.Manifest,
                        progress: () =>
                        {
                            Interlocked.Increment(ref job.Completed);
                            Changed?.Invoke(job);
                        },
                        job.Cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // An empty outcome makes the UI say "0 converted, 0 failed",
                    // which reads as though nothing happened. Every input gets
                    // an outcome, here as well as inside RunAsync.
                    outcome = new ConversionService.JobOutcome(
                        job.Manifest.Verb, OutcomeForAll(job, "cancelled"));
                }
                catch (Exception ex)
                {
                    Storage.Log($"JobQueue: {ex.GetType().Name}: {ex.Message}");
                    outcome = new ConversionService.JobOutcome(
                        job.Manifest.Verb, OutcomeForAll(job, ex.Message));
                }

                job.Failed = outcome.FailedCount;
                _current = null;

                try
                {
                    Finished?.Invoke(job, outcome);
                }
                finally
                {
                    // The token source holds a wait handle; one per job adds up
                    // over a long-running resident Host.
                    job.Cancellation.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public void Dispose()
    {
        try
        {
            CancelAll();
            _pending.CompleteAdding();
            _shutdown.Cancel();
            _pump.Wait(2000);
        }
        catch { /* shutdown is best effort */ }

        _shutdown.Dispose();
        _pending.Dispose();
    }
}
