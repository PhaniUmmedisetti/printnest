using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PrintNest.Application.Interfaces;
using PrintNest.Infrastructure.Persistence;
using PrintNest.Infrastructure.Workers;

namespace PrintNest.IntegrationTests.Infrastructure;

internal sealed class PrintNestWebAppFactory : WebApplicationFactory<Program>
{
    private readonly IntegrationContainerFixture _fixture;
    private readonly int? _jwtFileTokenTtlSeconds;
    private readonly IReleaseConcurrencyTestHook? _concurrencyHook;

    public PrintNestWebAppFactory(
        IntegrationContainerFixture fixture,
        int? jwtFileTokenTtlSeconds,
        IReleaseConcurrencyTestHook? concurrencyHook)
    {
        _fixture = fixture;
        _jwtFileTokenTtlSeconds = jwtFileTokenTtlSeconds;
        _concurrencyHook = concurrencyHook;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _fixture.PostgresConnectionString,
                ["Jwt:SigningKey"] = _fixture.JwtSigningKey,
                ["Jwt:FileTokenTtlSeconds"] = (_jwtFileTokenTtlSeconds ?? 120).ToString(),
                ["AdminApiKey"] = _fixture.AdminApiKey,
                ["Storage:Endpoint"] = _fixture.MinioEndpoint,
                ["Storage:AccessKey"] = _fixture.MinioAccessKey,
                ["Storage:SecretKey"] = _fixture.MinioSecretKey,
                ["Storage:BucketName"] = _fixture.BucketName,
                ["Storage:UseHttps"] = "false",
                ["Cors:AllowedOrigins"] = "http://localhost:3000"
            };

            config.AddInMemoryCollection(values);
        });

        builder.ConfigureServices(services =>
        {
            var workerHostedServices = services
                .Where(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType is not null &&
                    (d.ImplementationType == typeof(ExpiryWorker) ||
                     d.ImplementationType == typeof(CleanupWorker)))
                .ToList();

            foreach (var descriptor in workerHostedServices)
                services.Remove(descriptor);

            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(
                    _fixture.PostgresConnectionString,
                    npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 3)));

            services.AddSingleton<ExpiryWorker>();
            services.AddSingleton<CleanupWorker>();

            if (_concurrencyHook is not null)
            {
                services.RemoveAll<IReleaseConcurrencyTestHook>();
                services.AddSingleton(_concurrencyHook);
            }
        });
    }
}
