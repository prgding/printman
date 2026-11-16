using Printman.Core.Abstractions;

namespace Printman.Tests.Fakes;

/// <summary>No-op renderer that supports a configurable set of extensions.</summary>
public sealed class FakeDocumentRenderer(params string[] extensions) : IDocumentRenderer
{
    public string[] Extensions { get; } = extensions;
    public int PageCount { get; set; } = 1;

    public bool CanHandle(string fileExtension) =>
        Extensions.Contains(fileExtension, StringComparer.OrdinalIgnoreCase);

    public Task<int> GetPageCountAsync(string filePath) => Task.FromResult(PageCount);

}
