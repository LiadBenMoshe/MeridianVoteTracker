using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace Meridian.Infrastructure;

/// <summary>
/// One Entra ID identity for all Azure resources (PostgreSQL, Blob) — no keys or passwords.
/// Azure:Credential = "Default" (managed identity, service principal env vars, VS / az login)
///                  | "DeviceCode" (inside Docker on a dev machine: sign in once via microsoft.com/devicelogin).
/// </summary>
public sealed class AzureCredentials
{
    public const string PostgresScope = "https://ossrdbms-aad.database.windows.net/.default";
    public const string StorageScope = "https://storage.azure.com/.default";

    private readonly string _recordPath;

    public TokenCredential Credential { get; }
    public bool IsDeviceCode { get; }

    public AzureCredentials(IConfiguration config)
    {
        var tenant = config["Azure:TenantId"];
        IsDeviceCode = string.Equals(config["Azure:Credential"], "DeviceCode", StringComparison.OrdinalIgnoreCase);
        var dir = config["Azure:AuthCachePath"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".IdentityService");
        _recordPath = Path.Combine(dir, "meridian-auth-record.json");

        if (!IsDeviceCode)
        {
            Credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = tenant });
            return;
        }

        // Token cache on disk (a Docker volume in docker-compose), so a restart does not need a new sign-in,
        // and one sign-in covers Blob, PostgreSQL and Document Intelligence (silent refresh).
        // Dev-only convenience: containers have no OS keyring, so the cache file is not encrypted.
        Credential = new DeviceCodeCredential(new DeviceCodeCredentialOptions
        {
            TenantId = tenant,
            TokenCachePersistenceOptions = new FileTokenCache(Path.Combine(dir, "meridian-token-cache.bin")),
            AuthenticationRecord = LoadRecord(),
            DeviceCodeCallback = (code, _) =>
            {
                Console.WriteLine($"\n==== AZURE SIGN-IN NEEDED ====\n{code.Message}\n==============================\n");
                return Task.CompletedTask;
            },
        });
    }

    public const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>
    /// Device code only: sign in once at startup (not inside a web request, which could time out),
    /// then get a token for every Azure resource the app uses, so later calls refresh silently.
    /// </summary>
    public async Task EnsureSignedInAsync(IReadOnlyList<string> scopes, CancellationToken ct)
    {
        if (!IsDeviceCode || scopes.Count == 0) return;
        if (!File.Exists(_recordPath))
        {
            var record = await ((DeviceCodeCredential)Credential).AuthenticateAsync(new TokenRequestContext([scopes[0]]), ct);
            Directory.CreateDirectory(Path.GetDirectoryName(_recordPath)!);
            await using var f = File.Create(_recordPath);
            await record.SerializeAsync(f, ct);
            Console.WriteLine($"Azure sign-in OK as {record.Username}");
        }
        foreach (var scope in scopes)
            await Credential.GetTokenAsync(new TokenRequestContext([scope]), ct);
    }

    /// <summary>Which Azure resources this configuration talks to with the Entra identity.</summary>
    public static List<string> ScopesFor(IConfiguration config)
    {
        var scopes = new List<string>();
        if (!string.Equals(config["Storage:Provider"], "Local", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(config["Storage:AccountUrl"]) && string.IsNullOrWhiteSpace(config["Storage:ContainerSasUrl"]))
            scopes.Add(StorageScope);
        if (string.Equals(config["Database:Auth"], "Entra", StringComparison.OrdinalIgnoreCase))
            scopes.Add(PostgresScope);
        if (!string.IsNullOrWhiteSpace(config["DocumentIntelligence:Endpoint"]) && string.IsNullOrWhiteSpace(config["DocumentIntelligence:Key"]))
            scopes.Add(CognitiveServicesScope);
        return scopes;
    }

    private AuthenticationRecord? LoadRecord()
    {
        if (!File.Exists(_recordPath)) return null;
        using var f = File.OpenRead(_recordPath);
        return AuthenticationRecord.Deserialize(f);
    }
}

/// <summary>Plain file token cache (containers have no OS keyring). Dev use only; the file lives in a Docker volume.</summary>
internal sealed class FileTokenCache(string path) : UnsafeTokenCacheOptions
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    protected override async Task<ReadOnlyMemory<byte>> RefreshCacheAsync()
    {
        await _lock.WaitAsync();
        try { return File.Exists(path) ? await File.ReadAllBytesAsync(path) : ReadOnlyMemory<byte>.Empty; }
        finally { _lock.Release(); }
    }

    protected override async Task TokenCacheUpdatedAsync(TokenCacheUpdatedArgs args)
    {
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, args.UnsafeCacheData.ToArray());
        }
        finally { _lock.Release(); }
    }
}
