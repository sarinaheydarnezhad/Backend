using System.Diagnostics;
using System.Text.Json;
using Cent.Module;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

// No production connection or caller-provided database is accepted. This probe
// creates only a new Itatest-named database on the local disposable instance.
if (args.FirstOrDefault() == "--sqlite") { await SqliteReaderBenchmark.Run(args[1]); return; }
var output = Path.GetFullPath(args.FirstOrDefault() ?? throw new ArgumentException("Pass the private verification output directory."));
Directory.CreateDirectory(output);
var name = "ItatestReaderPhase4_" + Guid.NewGuid().ToString("N");
var connection = new SqlConnectionStringBuilder { DataSource = @"(localdb)\ItatestPhase4", InitialCatalog = name,
    IntegratedSecurity = true, TrustServerCertificate = true, ConnectTimeout = 5 }.ToString();
CentDbContext Db() => new(new DbContextOptionsBuilder<CentDbContext>().UseSqlServer(connection, sql => sql.MigrationsHistoryTable("cent_migrations_history")).Options);
var watch = Stopwatch.StartNew();
try {
    await using var db = Db();
    var script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
    await File.WriteAllTextAsync(Path.Combine(output, "cent-migrations.sql"), script);
    if (db.Database.HasPendingModelChanges()) throw new InvalidOperationException("Cent migration snapshot has pending model changes.");
    await db.Database.MigrateAsync();
    var migrated = watch.ElapsedMilliseconds;
    await new CentCurriculumImporter(db).Import(); var imported = watch.ElapsedMilliseconds;
    await new CentCurriculumImporter(db).Import();
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Cent:Reader:Protection:AccountConcurrent"]="3" }).Build();
    var user = Guid.NewGuid(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var ready = Enumerable.Range(0,12).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
    var accepted = 0; var rejected = 0;
    var tasks = Enumerable.Range(0,12).Select(async i => {
        await using var context = Db(); IAsyncDisposable? lease = null;
        try {
            lease = await new CentReaderProtection(context, config, NullLogger<CentReaderProtection>.Instance, TimeProvider.System).Enter(user,"isolated-probe-"+i,Guid.NewGuid(),false,default);
            Interlocked.Increment(ref accepted);
        } catch (CentException e) when (e.Status==429) { Interlocked.Increment(ref rejected); }
        finally { ready[i].SetResult(); }
        await release.Task;
        if (lease!=null) await lease.DisposeAsync();
    }).ToArray();
    await Task.WhenAll(ready.Select(r=>r.Task)); release.SetResult(); await Task.WhenAll(tasks);
    if (accepted>3 || accepted<1 || accepted+rejected!=12) throw new InvalidOperationException("SQL Server concurrency protection did not enforce its bound.");
    await db.Database.OpenConnectionAsync(); await using var size = db.Database.GetDbConnection().CreateCommand();
    size.CommandText="SELECT SUM(CAST(size AS bigint))*8192 FROM sys.database_files";
    var bytes=Convert.ToInt64(await size.ExecuteScalarAsync());
    await File.WriteAllTextAsync(Path.Combine(output,"sqlserver-results.json"),JsonSerializer.Serialize(new { passed=true, server=@"(localdb)\ItatestPhase4", database=name, migratedMs=migrated, importMs=imported-migrated, elapsedMs=watch.ElapsedMilliseconds, databaseBytes=bytes, accepted,rejected, questions=await db.Questions.CountAsync() },new JsonSerializerOptions { WriteIndented=true }));
} catch(Exception error) {
    await File.WriteAllTextAsync(Path.Combine(output,"sqlserver-results.json"),JsonSerializer.Serialize(new { passed=false,database=name,elapsedMs=watch.ElapsedMilliseconds,error=error.Message },new JsonSerializerOptions { WriteIndented=true }));
    Console.Error.WriteLine(error.Message); Environment.ExitCode=1;
}
