using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Cent.Module;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class SqliteReaderBenchmark
{
    public static async Task Run(string reportDirectory)
    {
        var output=Path.GetFullPath(reportDirectory);
        var directory=Path.GetFullPath(Path.Combine(output,"../../../../.preview-artifacts/reader-phase4/benchmarks"));
        Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"isolated-"+Guid.NewGuid().ToString("N")+".db");
        if(File.Exists(path)) throw new InvalidOperationException("Benchmark database must be new.");
        await using var db=new BenchmarkDb(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(new SqliteConnectionStringBuilder {DataSource=path}.ToString()).Options);
        await db.Database.EnsureCreatedAsync();var watch=Stopwatch.StartNew();
        await new CentCurriculumImporter(db).Import(includeReviewed:false);var legacyMs=watch.ElapsedMilliseconds;
        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)");var beforeBytes=new FileInfo(path).Length;
        var backup=path+".before-reviewed.bak";
        using(var source=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=path}.ToString()))
        using(var copy=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=backup,Pooling=false}.ToString())) {
            source.Open();copy.Open();source.BackupDatabase(copy);
            using var check=copy.CreateCommand();check.CommandText="PRAGMA integrity_check";
            if(Convert.ToString(check.ExecuteScalar())!="ok") throw new InvalidOperationException("Backup integrity failed.");
        }
        var backupHash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(backup)));
        watch.Restart();await new CentCurriculumImporter(db).Import();var upgradeMs=watch.ElapsedMilliseconds;
        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)");var afterBytes=new FileInfo(path).Length;
        watch.Restart();await new CentCurriculumImporter(db).Import();var repeatMs=watch.ElapsedMilliseconds;
        var config=new ConfigurationBuilder().Build();var user=Guid.NewGuid();
        var guard=new CentReaderProtection(db,config,NullLogger<CentReaderProtection>.Instance,TimeProvider.System);
        var timings=new List<double>();
        for(var i=0;i<50;i++) {watch.Restart();await using(await guard.Enter(user,"benchmark-loopback",Guid.Empty,true,default)) {} timings.Add(watch.Elapsed.TotalMilliseconds);}
        var budgetBytes=(await db.Set<CentReaderBudget>().Select(b=>b.StateJson).ToArrayAsync()).Sum(s=>System.Text.Encoding.UTF8.GetByteCount(s));
        var beforeCleanup=await db.Set<CentReaderBudget>().CountAsync();
        var removed=await new CentReaderCleanup(db,config,new FutureClock()).Run(default);
        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)");
        await File.WriteAllTextAsync(Path.Combine(output,"sqlite-performance.json"),JsonSerializer.Serialize(new {passed=true,provider="SQLite isolated development, not SQL Server certification",legacyImportMs=legacyMs,reviewedUpgradeMs=upgradeMs,idempotentRepeatMs=repeatMs,beforeBytes,afterBytes,growthBytes=afterBytes-beforeBytes,assetRetrievals=50,meanGuardMs=timings.Average(),p95GuardMs=timings.Order().ElementAt(47),budgetRows=beforeCleanup,budgetJsonBytes=budgetBytes,expiredRowsRemoved=removed,rowsAfterCleanup=await db.Set<CentReaderBudget>().CountAsync(),backupFile=Path.GetFileName(backup),backupSha256=backupHash,backupIntegrity="ok"},new JsonSerializerOptions {WriteIndented=true}));
    }
    private sealed class FutureClock:TimeProvider {public override DateTimeOffset GetUtcNow()=>DateTimeOffset.UtcNow.AddHours(2);}
    private sealed class BenchmarkDb(DbContextOptions<CentDbContext> options):CentDbContext(options) {
        protected override void OnModelCreating(ModelBuilder builder) {
            base.OnModelCreating(builder);
            var converter=new ValueConverter<DateTimeOffset,long>(v=>v.UtcTicks,v=>new DateTimeOffset(v,TimeSpan.Zero));
            foreach(var property in builder.Model.GetEntityTypes().SelectMany(e=>e.GetProperties()))
                if((Nullable.GetUnderlyingType(property.ClrType)??property.ClrType)==typeof(DateTimeOffset)) property.SetValueConverter(converter);
        }
    }
}
