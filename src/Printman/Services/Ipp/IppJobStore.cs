using System.Security.Cryptography;
using Printman.Core.Abstractions;
using Printman.Core.Models;

namespace Printman.Services.Ipp;

public class IppJobStore : IIppJobStore
{
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetainFinishedFor = TimeSpan.FromHours(1);
    private const int MaxFinishedJobs = 100;

    private readonly Dictionary<int, IppJob> _jobs = new();
    private readonly object _sync = new();

    public IppJob Create(string printerSlug, string name, string userName, IppJobOptions options, string? sourceAddress = null, int maxActiveJobs = 50)
    {
        lock (_sync)
        {
            Sweep();
            // The limit applies across every printer and includes uploads awaiting their document.
            if (_jobs.Values.Count(j => !IppJobState.IsTerminal(j.GetState().State)) >= maxActiveJobs)
                throw new IppQueueFullException();
            int id;
            do { id = RandomNumberGenerator.GetInt32(1, int.MaxValue); } while (_jobs.ContainsKey(id));
            var job = new IppJob
            {
                Id = id,
                SourceAddress = sourceAddress,
                PrinterSlug = printerSlug,
                Name = name,
                UserName = userName,
                Options = options
            };
            _jobs[job.Id] = job;
            return job;
        }
    }

    public IppJob? Get(int jobId)
    {
        lock (_sync)
        {
            Sweep();
            return _jobs.TryGetValue(jobId, out var job) ? job : null;
        }
    }

    public IReadOnlyList<IppJob> List(string printerSlug)
    {
        lock (_sync)
        {
            Sweep();
            return _jobs.Values
                .Where(j => j.PrinterSlug == printerSlug)
                .OrderBy(j => j.CreatedAt)
                .ToList();
        }
    }

    public int ActiveCount(string printerSlug)
    {
        lock (_sync)
        {
            Sweep();
            return _jobs.Values.Count(j => j.PrinterSlug == printerSlug && !IppJobState.IsTerminal(j.GetState().State));
        }
    }

    private void Sweep()
    {
        var now = DateTime.UtcNow;

        // Create-Job that never received its document
        foreach (var job in _jobs.Values)
        {
            if (job.AwaitingDocument && now - job.CreatedAt > AbandonedAfter)
            {
                job.Terminate(IppJobState.Aborted, "aborted-by-system");
            }
        }

        var finished = _jobs.Values
            .Where(j => IppJobState.IsTerminal(j.GetState().State))
            .OrderByDescending(j => j.CreatedAt)
            .ToList();

        for (int i = 0; i < finished.Count; i++)
        {
            var job = finished[i];
            if (i >= MaxFinishedJobs || (job.CompletedAt is DateTime done && now - done > RetainFinishedFor))
            {
                _jobs.Remove(job.Id);
            }
        }
    }
}

public sealed class IppQueueFullException() : Exception("Print queue is full.");
