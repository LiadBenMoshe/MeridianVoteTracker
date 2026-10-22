using Meridian.Infrastructure.Data;
using Meridian.Infrastructure.Extraction;
using Meridian.Infrastructure.Imports;
using Meridian.Infrastructure.Jobs;
using Meridian.Infrastructure.Snapshots;
using Meridian.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Meridian.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddMeridianInfrastructure(this IServiceCollection services, IConfiguration config, bool runWorkers = true)
    {
        var cs = config.GetConnectionString("Meridian");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("ConnectionStrings:Meridian is not configured (set ConnectionStrings__Meridian, user-secrets or Key Vault).");

        AzureCredentials? azure = null;
        AzureCredentials Azure() => azure ??= new AzureCredentials(config);
        services.AddSingleton(_ => Azure());
        services.AddSingleton(_ => Azure().Credential);

        // One data source for EF and raw Npgsql (COPY, streaming).
        var builder = new NpgsqlDataSourceBuilder(cs);
        if (string.Equals(config["Database:Auth"], "Entra", StringComparison.OrdinalIgnoreCase))
        {
            // Microsoft Entra login to Azure Database for PostgreSQL: the "password" is a short-lived token, refreshed automatically.
            builder.UsePeriodicPasswordProvider(async (_, ct) =>
                    (await Azure().Credential.GetTokenAsync(new Azure.Core.TokenRequestContext([AzureCredentials.PostgresScope]), ct)).Token,
                TimeSpan.FromMinutes(45), TimeSpan.FromSeconds(10));
        }
        var dataSource = builder.Build();
        services.AddSingleton(dataSource);
        services.AddDbContext<MeridianDbContext>(o => o
            .UseNpgsql(dataSource, npgsql => npgsql.CommandTimeout(300))
            .UseSnakeCaseNamingConvention());

        services.Configure<StorageOptions>(config.GetSection("Storage"));
        if (string.Equals(config["Storage:Provider"], "Local", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IFileStore, LocalFileStore>();
        else
            services.AddSingleton<IFileStore, AzureBlobFileStore>();

        services.Configure<DocumentIntelligenceOptions>(config.GetSection("DocumentIntelligence"));
        services.AddSingleton<ILayoutReader, DocumentIntelligenceReader>();

        services.AddScoped<DatabaseInitializer>();
        services.AddScoped<ImportProcessor>();
        services.AddScoped<SnapshotCalculator>();
        services.AddScoped<ExtractionProcessor>();
        services.AddSingleton<JobSignal>();

        if (runWorkers)
        {
            services.AddHostedService<ImportWorker>();
            services.AddHostedService<SnapshotWorker>();
            services.AddHostedService<ExtractionWorker>();
        }
        return services;
    }
}
