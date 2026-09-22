using System.Collections.Concurrent;
using MAI.BusinessLogic.Interfaces;

namespace MAI.IntegrationTests;

/// <summary>
/// Depozit de fisiere in memorie: inlocuieste MinIO in testele de integrare.
///
/// Thread-safe (ConcurrentDictionary): testele pot rula in paralel daca nu
/// impart stare. Nu cripteaza nimic - testele nu au nevoie de criptarea la
/// nivel de aplicatie, ci de comportamentul controllerelor.
/// </summary>
public sealed class InMemoryFileStorage : IFileStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _store = new();

    public string ProviderName => "InMemory (test)";
    public bool SupportsPresignedUrls => false;

    public Task PutAsync(string key, Stream content, long contentLength, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        _store[key] = ms.ToArray();
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        if (!_store.TryGetValue(key, out var data))
            throw new FileNotFoundException($"Cheia '{key}' nu exista in depozitul de test.");
        return Task.FromResult<Stream>(new MemoryStream(data));
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_store.ContainsKey(key));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<string?> TryCreatePresignedDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<bool> HealthCheckAsync(CancellationToken ct = default)
        => Task.FromResult(true);

    // ── Helpers pentru asertii ────────────────────────────────────────────

    public bool Contains(string key) => _store.ContainsKey(key);
    public int Count => _store.Count;
}
