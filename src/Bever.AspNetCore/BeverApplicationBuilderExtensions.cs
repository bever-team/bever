using Bever.Migrations;
using Bever.Sqlite;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bever.AspNetCore;

public static class BeverApplicationBuilderExtensions
{
    public static IServiceCollection AddBever(this IServiceCollection services, Action<BeverOptions>? configure = null)
    {
        // The "Bever" configuration section (appsettings, environment variables) sets options; code wins.
        services.AddOptions<BeverOptions>().BindConfiguration("Bever");
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddSingleton<BeverModelLoader>();
        services.AddSingleton(serviceProvider => serviceProvider.GetRequiredService<BeverModelLoader>().Load());
        services.AddSingleton<BeverRuntime>();
        services.AddSingleton(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<BeverOptions>>().Value;
            var environment = serviceProvider.GetRequiredService<IHostEnvironment>();
            var databasePath = Path.IsPathRooted(options.DatabasePath)
                ? options.DatabasePath
                : Path.Combine(environment.ContentRootPath, options.DatabasePath);
            var model = serviceProvider.GetRequiredService<Bever.Model.BeverModel>();
            var schema = new PersistenceSchemaProjector().Project(model);
            return new BeverSqliteDatabase(databasePath, schema);
        });
        services.AddSingleton<BeverEntityRequestNormalizer>();
        services.AddSingleton<BeverEntityValidator>();
        services.AddHttpContextAccessor();
        services.AddAntiforgery();
        services.AddAuthorization();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
            });
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<IHostEnvironment>((options, environment) =>
            {
                options.Cookie.SecurePolicy = environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
            });
        services.AddSingleton<BeverAuthorizationEvaluator>();
        services.AddSingleton<BeverReferences>();
        services.TryAddSingleton<IBeverDatabaseProvider, SqliteBeverDatabaseProvider>();
        services.TryAddSingleton<IBeverIdentityStore>(serviceProvider =>
            serviceProvider.GetRequiredService<IBeverDatabaseProvider>() is SqliteBeverDatabaseProvider
                ? new SqliteBeverIdentityStore(serviceProvider.GetRequiredService<BeverSqliteDatabase>())
                : new InMemoryBeverIdentityStore());
        services.AddHostedService<BeverStartupValidator>();

        return services;
    }
}
