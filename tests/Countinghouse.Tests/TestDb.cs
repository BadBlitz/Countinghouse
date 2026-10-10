using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;

namespace Countinghouse.Tests;

// SQLite in-memory database shared across contexts for one test's lifetime.
// The database lives as long as the open connection.
public sealed class TestDb : IDbContextFactory<CountinghouseContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CountinghouseContext> _options;

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<CountinghouseContext>()
            .UseSqlite(_connection)
            .Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public CountinghouseContext CreateDbContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}
