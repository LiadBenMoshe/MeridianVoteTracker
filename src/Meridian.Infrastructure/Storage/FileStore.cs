using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace Meridian.Infrastructure.Storage;

/// <summary>Where uploaded CSV/PDF files are kept (HIS-01: the original file is always retrievable).</summary>
public interface IFileStore
{
    Task SaveAsync(string path, Stream content, string contentType, CancellationToken ct);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);
}

public sealed class StorageOptions
{
    /// <summary>"AzureBlob" (default) or "Local" (tests / offline dev).</summary>
    public string Provider { get; set; } = "AzureBlob";
    /// <summary>Blob connection string (or use AccountUrl + managed identity / az login).</summary>
    public string? ConnectionString { get; set; }
    public string? AccountUrl { get; set; }
    /// <summary>
    /// Container SAS URL (e.g. a user-delegation SAS made in the Portal with your own Entra login).
    /// Lets the app use Blob without account keys. The container must already exist.
    /// </summary>
    public string? ContainerSasUrl { get; set; }
    public string Container { get; set; } = "uploads";
    public string LocalPath { get; set; } = "data/uploads";
}

public sealed class AzureBlobFileStore : IFileStore
{
    private readonly BlobContainerClient _container;
    private bool _ensured;

    public AzureBlobFileStore(IOptions<StorageOptions> options, Azure.Core.TokenCredential credential)
    {
        var o = options.Value;
        if (!string.IsNullOrWhiteSpace(o.ContainerSasUrl))
        {
            _container = new BlobContainerClient(new Uri(o.ContainerSasUrl));
            _ensured = true; // a container-scoped SAS cannot create containers
            return;
        }
        var service = !string.IsNullOrWhiteSpace(o.ConnectionString)
            ? new BlobServiceClient(o.ConnectionString)
            : !string.IsNullOrWhiteSpace(o.AccountUrl)
                ? new BlobServiceClient(new Uri(o.AccountUrl), credential)
                : throw new InvalidOperationException("Storage:ContainerSasUrl, Storage:ConnectionString or Storage:AccountUrl must be configured for Azure Blob storage.");
        _container = service.GetBlobContainerClient(o.Container);
    }

    private async Task EnsureAsync(CancellationToken ct)
    {
        if (_ensured) return;
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        _ensured = true;
    }

    public async Task SaveAsync(string path, Stream content, string contentType, CancellationToken ct)
    {
        await EnsureAsync(ct);
        await _container.GetBlobClient(path).UploadAsync(content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        await EnsureAsync(ct);
        return await _container.GetBlobClient(path).OpenReadAsync(cancellationToken: ct);
    }
}

public sealed class LocalFileStore(IOptions<StorageOptions> options) : IFileStore
{
    private string Full(string path) => Path.GetFullPath(Path.Combine(options.Value.LocalPath, path));

    public async Task SaveAsync(string path, Stream content, string contentType, CancellationToken ct)
    {
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var f = File.Create(full);
        await content.CopyToAsync(f, ct);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) =>
        Task.FromResult<Stream>(File.OpenRead(Full(path)));
}
