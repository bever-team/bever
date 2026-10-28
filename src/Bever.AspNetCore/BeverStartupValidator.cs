using Bever.Sqlite;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bever.AspNetCore;

internal sealed class BeverStartupValidator : IHostedService
{
    private readonly BeverRuntime runtime;
    private readonly IBeverDatabaseProvider provider;
    private readonly IBeverIdentityStore identityStore;
    private readonly IServiceProvider serviceProvider;
    private readonly IHostEnvironment environment;
    private readonly IOptionsMonitor<CookieAuthenticationOptions> cookieOptions;
    private readonly ILogger<BeverStartupValidator> logger;

    public BeverStartupValidator(
        BeverRuntime runtime,
        IBeverDatabaseProvider provider,
        IBeverIdentityStore identityStore,
        IServiceProvider serviceProvider,
        IHostEnvironment environment,
        IOptionsMonitor<CookieAuthenticationOptions> cookieOptions,
        ILogger<BeverStartupValidator> logger)
    {
        this.runtime = runtime;
        this.provider = provider;
        this.identityStore = identityStore;
        this.serviceProvider = serviceProvider;
        this.environment = environment;
        this.cookieOptions = cookieOptions;
        this.logger = logger;
    }

    private async Task ValidateAsync(CancellationToken cancellationToken)
    {
        _ = runtime.Entities;
        if (runtime.Model.Identity.Enabled && !environment.IsDevelopment())
        {
            if (provider is not SqliteBeverDatabaseProvider || identityStore is not SqliteBeverIdentityStore)
            {
                throw new InvalidOperationException("Bever identity requires the SQLite-backed identity store outside local development.");
            }

            var cookie = cookieOptions.Get(CookieAuthenticationDefaults.AuthenticationScheme).Cookie;
            if (cookie.SecurePolicy != CookieSecurePolicy.Always)
            {
                throw new InvalidOperationException("Bever identity cookies must be Secure outside local development.");
            }
        }

        if (provider is not SqliteBeverDatabaseProvider)
        {
            return;
        }

        var sqliteDatabase = serviceProvider.GetRequiredService<BeverSqliteDatabase>();
        var status = await sqliteDatabase.GetSchemaStatusAsync(cancellationToken);
        if (status != BeverSqliteSchemaStatus.Current)
        {
            logger.LogCritical("Bever SQLite database schema status is {SchemaStatus}. Database operations may fail until migrations are applied.", status);
        }

        if (runtime.Model.Identity.Enabled)
        {
            var identityStatus = await sqliteDatabase.GetIdentitySchemaStatusAsync(cancellationToken);
            if (identityStatus != BeverSqliteIdentitySchemaStatus.Current)
            {
                logger.LogCritical("Bever SQLite identity schema status is {SchemaStatus}.", identityStatus);
                if (!environment.IsDevelopment())
                {
                    throw new InvalidOperationException($"Bever identity schema status is '{identityStatus}'.");
                }
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => ValidateAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
