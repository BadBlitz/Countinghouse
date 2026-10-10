using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;

namespace Countinghouse.Tests;

// A real on-disk SQLite database in a temp directory. The in-memory TestDb can't be used for
// backup tests: DatabaseBackupService opens its own connection to a database *path* to run
// VACUUM INTO, which an ":memory:" database doesn't have.
internal sealed class FileTestDb : IDbContextFactory<CountinghouseContext>, IDisposable
{
    public string Root { get; }
    public string DbPath { get; }
    public string StagingDirectory { get; }
    public string DestinationDirectory { get; }

    private readonly DbContextOptions<CountinghouseContext> _options;

    public FileTestDb()
    {
        Root = Path.Combine(Path.GetTempPath(), "countinghouse-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        DbPath = Path.Combine(Root, "countinghouse.db");
        StagingDirectory = Path.Combine(Root, "staging");
        // Stands in for the NAS share; a local directory exercises the same copy path.
        DestinationDirectory = Path.Combine(Root, "destination");

        _options = new DbContextOptionsBuilder<CountinghouseContext>()
            .UseSqlite($"Data Source={DbPath}")
            .Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public CountinghouseContext CreateDbContext() => new(_options);

    public void Dispose()
    {
        // Pooled connections keep the file handle open, which blocks the directory delete.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // A temp directory left behind is not worth failing a test over.
        }
    }
}
