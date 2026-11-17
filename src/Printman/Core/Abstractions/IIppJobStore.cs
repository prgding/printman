using Printman.Core.Models;

namespace Printman.Core.Abstractions;

/// <summary>
/// In-memory registry of IPP jobs with unpredictable positive job ids.
/// </summary>
public interface IIppJobStore
{
    IppJob Create(string printerSlug, string name, string userName, IppJobOptions options, string? sourceAddress = null, int maxActiveJobs = 50);
    IppJob? Get(int jobId);
    IReadOnlyList<IppJob> List(string printerSlug);

    /// <summary>Number of jobs on the printer that have not reached a terminal state.</summary>
    int ActiveCount(string printerSlug);
}
