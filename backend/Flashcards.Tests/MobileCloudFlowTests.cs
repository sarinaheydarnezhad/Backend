using System.Diagnostics;
using System.Data.Common;
using Flashcards.Infrastructure.Backups;
using Flashcards.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flashcards.Tests;

public sealed class MobileCloudFlowFactAttribute : FactAttribute
{
    public MobileCloudFlowFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LERONA_FRONTEND_DIR")))
            Skip = "Set LERONA_FRONTEND_DIR to run the real frontend/backend HTTP regression.";
    }
}

public sealed class MobileCloudFlowTests
{
    [MobileCloudFlowFact]
    public async Task Mobile_backup_logout_and_fresh_install_use_the_real_api_and_stored_snapshot()
    {
        var frontend = Path.GetFullPath(Environment.GetEnvironmentVariable("LERONA_FRONTEND_DIR")!);
        // Keep the isolated database alive, while each HTTP request gets its own connection.
        await using var connection = new SqliteConnection($"Data Source=cloud-flows-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
        await connection.OpenAsync();
        var failure = new ControlledProtector();
        await using var factory = new FlowFactory(connection, failure);
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Database.EnsureCreatedAsync();
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = frontend, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { "node_modules/jest/bin/jest.js", "--runInBand", "--runTestsByPath",
                     "test/cloud-account-flows.e2e.ts", "--testMatch", "**/cloud-account-flows.e2e.ts" })
            start.ArgumentList.Add(argument);
        start.Environment["LERONA_FLOW_API_URL"] = client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        var diagnostics = (await stdout) + (await stderr);
        var evidence = Directory.CreateDirectory(Path.Combine(frontend, ".tools"));
        await File.WriteAllTextAsync(Path.Combine(evidence.FullName, "cloud-account-flows.http.log"), diagnostics);
        Assert.True(process.ExitCode == 0, diagnostics);
        using var inspect = factory.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.True(await stored.UserBackups.CountAsync(item => item.Status == "Completed" && item.Payload != null) >= 2);
    }

    private sealed class FlowFactory(SqliteConnection connection, ControlledProtector failure) : TestApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<FlashcardsDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<FlashcardsDbContext>>();
                services.AddDbContext<FlashcardsDbContext>(options => options.UseSqlite(connection.ConnectionString)
                    .ReplaceService<IModelCustomizer, FlowModelCustomizer>().AddInterceptors(new FlowSqliteFunctions()));
                services.RemoveAll<BackupSettings>();
                services.AddSingleton(new BackupSettings(30, 10485760, 20, Path.GetTempPath()));
                services.RemoveAll<IBackupPayloadProtector>();
                services.AddSingleton<IBackupPayloadProtector>(failure);
                services.AddSingleton<IStartupFilter>(new FailureControl(failure));
            });
        }
    }

    private sealed class FlowSqliteFunctions : DbConnectionInterceptor
    {
        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            ((SqliteConnection)connection).CreateFunction("ISJSON", (string value) =>
            {
                try { using var json = System.Text.Json.JsonDocument.Parse(value); return 1; }
                catch (System.Text.Json.JsonException) { return 0; }
            });
            return Task.CompletedTask;
        }
    }

    private sealed class FlowModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            var dates = new ValueConverter<DateTimeOffset, long>(value => value.UtcTicks,
                value => new DateTimeOffset(value, TimeSpan.Zero));
            foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(item => item.GetProperties()))
                if ((Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(dates);
                    property.SetColumnType("bigint");
                }
            modelBuilder.Entity<Flashcards.Domain.UserBackup>().Property(item => item.Payload).HasColumnType("BLOB");
        }
    }

    private sealed class ControlledProtector : IBackupPayloadProtector
    {
        private readonly BackupPayloadProtector inner = new(new EphemeralDataProtectionProvider());
        public bool Fail { get; set; }
        public byte[] Protect(Guid userId, Guid backupId, int formatVersion, byte[] payload) => Fail
            ? throw new IOException("Injected isolated backup storage failure")
            : inner.Protect(userId, backupId, formatVersion, payload);
        public byte[] Unprotect(Guid userId, Guid backupId, int formatVersion, byte[] payload) =>
            inner.Unprotect(userId, backupId, formatVersion, payload);
    }

    // This control endpoint exists only in the isolated test host, never in the API project.
    private sealed class FailureControl(ControlledProtector failure) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                if (context.Request.Path == "/__test/backup-failure" && HttpMethods.IsPost(context.Request.Method))
                {
                    failure.Fail = context.Request.Query["enabled"] == "true";
                    context.Response.StatusCode = 204;
                    return;
                }
                await following();
            });
            next(app);
        };
    }
}
