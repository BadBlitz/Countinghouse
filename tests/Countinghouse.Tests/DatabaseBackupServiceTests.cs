using Microsoft.Data.Sqlite;
using Countinghouse.Domain.Entities;
using Countinghouse.Domain.Services;

namespace Countinghouse.Tests;

public class DatabaseBackupServiceTests : IDisposable
{
    private readonly FileTestDb _db = new();
    private readonly DatabaseBackupService _backups;
    private readonly AccountService _accounts;
    private readonly TransactionService _transactions;

    public DatabaseBackupServiceTests()
    {
        _backups = new DatabaseBackupService(_db, new BackupPaths(_db.DbPath, _db.StagingDirectory));
        _accounts = new AccountService(_db);
        _transactions = new TransactionService(_db);
    }

    public void Dispose() => _db.Dispose();

    private async Task<Account> SeedAsync()
    {
        var account = await _accounts.CreateAccountAsync(new Account
        {
            Name = "Seeded Checking",
            AccountType = AccountType.Checking,
            OpeningBalance = 100m,
            OpeningBalanceDate = new DateOnly(2026, 1, 1),
        });
        await _transactions.AddTransactionAsync(new Transaction
        {
            AccountId = account.Id,
            Amount = -25m,
            PostedDate = new DateOnly(2026, 1, 5),
            Payee = "Seeded",
            Source = TransactionSource.Manual,
        });
        return account;
    }

    // ---- destination configuration

    [Fact]
    public async Task Destination_IsNullUntilSet()
    {
        Assert.Null(await _backups.GetDestinationAsync());
        Assert.False((await _backups.GetStatusAsync()).DestinationConfigured);
    }

    [Fact]
    public async Task SetDestination_RejectsBlank()
    {
        Assert.NotNull(await _backups.SetDestinationAsync("   "));
        Assert.Null(await _backups.GetDestinationAsync());
    }

    [Fact]
    public async Task SetDestination_CreatesTheFolderAndPersists()
    {
        var target = Path.Combine(_db.Root, "nested", "backups");

        var error = await _backups.SetDestinationAsync(target);

        Assert.Null(error);
        // Creating it up front is the point: a path that can't be written to isn't a destination.
        Assert.True(Directory.Exists(target));
        Assert.Equal(target, await _backups.GetDestinationAsync());
    }

    [Fact]
    public async Task SetDestination_RejectsAPathBlockedByAFile()
    {
        var blocked = Path.Combine(_db.Root, "not-a-folder");
        await File.WriteAllTextAsync(blocked, "x");

        Assert.NotNull(await _backups.SetDestinationAsync(blocked));
    }

    [Fact]
    public async Task SetDestination_Overwrites_RatherThanAddingASecondRow()
    {
        await _backups.SetDestinationAsync(Path.Combine(_db.Root, "first"));
        await _backups.SetDestinationAsync(Path.Combine(_db.Root, "second"));

        Assert.Equal(Path.Combine(_db.Root, "second"), await _backups.GetDestinationAsync());
        await using var db = _db.CreateDbContext();
        Assert.Single(db.AppSettings);
    }

    // ---- taking a backup

    [Fact]
    public async Task CreateBackup_WritesAVerifiedSnapshotToTheDestination()
    {
        await SeedAsync();
        await _backups.SetDestinationAsync(_db.DestinationDirectory);

        var outcome = await _backups.CreateBackupAsync();

        Assert.True(outcome.Succeeded);
        Assert.Null(outcome.Error);
        Assert.True(outcome.LocalCopyKept);

        var written = Directory.GetFiles(_db.DestinationDirectory, "countinghouse-*.db");
        Assert.Single(written);
        Assert.Equal(outcome.Run!.SizeBytes, new FileInfo(written[0]).Length);
        // Counts come from reopening the snapshot, so they prove it was verified, not assumed.
        Assert.Equal(1, outcome.Run.AccountCount);
        Assert.Equal(1, outcome.Run.TransactionCount);
    }

    [Fact]
    public async Task CreateBackup_SnapshotContainsTheDataAndOpensCleanly()
    {
        var account = await SeedAsync();
        await _backups.SetDestinationAsync(_db.DestinationDirectory);

        await _backups.CreateBackupAsync();

        var snapshot = Directory.GetFiles(_db.DestinationDirectory, "countinghouse-*.db").Single();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = snapshot, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", (await command.ExecuteScalarAsync())?.ToString());

        command.CommandText = $"SELECT Name FROM Accounts WHERE Id = {account.Id};";
        Assert.Equal("Seeded Checking", (await command.ExecuteScalarAsync())?.ToString());
    }

    [Fact]
    public async Task CreateBackup_AlsoKeepsALocalStagedCopy()
    {
        await SeedAsync();
        await _backups.SetDestinationAsync(_db.DestinationDirectory);

        await _backups.CreateBackupAsync();

        // The local copy is what makes an unreachable destination a partial failure rather than
        // no backup at all.
        Assert.Single(Directory.GetFiles(_db.StagingDirectory, "countinghouse-*.db"));
    }

    [Fact]
    public async Task CreateBackup_WithNoDestinationSet_KeepsTheSnapshotLocallyAndSaysSo()
    {
        await SeedAsync();

        var outcome = await _backups.CreateBackupAsync();

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.LocalCopyKept);
        Assert.Contains("destination", outcome.Error);
        Assert.Single(Directory.GetFiles(_db.StagingDirectory, "countinghouse-*.db"));
    }

    [Fact]
    public async Task CreateBackup_WhenTheDestinationIsUnreachable_ReportsPartialFailureNotLoss()
    {
        await SeedAsync();
        var destination = Path.Combine(_db.Root, "vanishing");
        await _backups.SetDestinationAsync(destination);
        // Simulates the NAS being asleep or off-network: the path saved fine, then stopped
        // working. Replacing the directory with a file makes the copy fail deterministically.
        Directory.Delete(destination);
        await File.WriteAllTextAsync(destination, "not a directory");

        var outcome = await _backups.CreateBackupAsync();

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.LocalCopyKept);
        Assert.NotNull(outcome.Error);
        // The snapshot itself succeeded, so the run still records what it contained.
        Assert.Equal(1, outcome.Run!.TransactionCount);
        Assert.Single(Directory.GetFiles(_db.StagingDirectory, "countinghouse-*.db"));
    }

    [Fact]
    public async Task CreateBackup_LogsEveryAttemptSuccessOrFailure()
    {
        await SeedAsync();
        await _backups.CreateBackupAsync();                                  // fails: no destination
        await _backups.SetDestinationAsync(_db.DestinationDirectory);
        await _backups.CreateBackupAsync();                                  // succeeds

        var status = await _backups.GetStatusAsync();

        Assert.Equal(2, status.Recent.Count);
        // Newest first.
        Assert.True(status.Recent[0].Succeeded);
        Assert.False(status.Recent[1].Succeeded);
    }

    // ---- the reminder

    [Fact]
    public async Task Status_ReminderIsDueWhenNothingHasEverBeenBackedUp()
    {
        var status = await _backups.GetStatusAsync();

        Assert.True(status.ReminderDue);
        Assert.Null(status.LastSuccessAt);
        Assert.Null(status.DaysSinceLastSuccess);
    }

    [Fact]
    public async Task Status_ReminderClearsAfterASuccessfulBackup()
    {
        await SeedAsync();
        await _backups.SetDestinationAsync(_db.DestinationDirectory);
        await _backups.CreateBackupAsync();

        var status = await _backups.GetStatusAsync();

        Assert.False(status.ReminderDue);
        Assert.NotNull(status.LastSuccessAt);
        Assert.Equal(0, status.DaysSinceLastSuccess);
    }

    [Fact]
    public async Task Status_ReminderReturnsOnceTheLastSuccessIsAWeekOld()
    {
        await using (var db = _db.CreateDbContext())
        {
            db.BackupRuns.Add(new BackupRun
            {
                CompletedAt = DateTime.Now.AddDays(-DatabaseBackupService.ReminderAfterDays - 1),
                Destination = _db.DestinationDirectory,
                FileName = "countinghouse-2026-01-01-000000.db",
                Succeeded = true,
            });
            await db.SaveChangesAsync();
        }

        var status = await _backups.GetStatusAsync();

        Assert.True(status.ReminderDue);
        Assert.True(status.DaysSinceLastSuccess >= DatabaseBackupService.ReminderAfterDays);
    }

    [Fact]
    public async Task Status_StaysQuietJustInsideTheReminderWindow()
    {
        await using (var db = _db.CreateDbContext())
        {
            db.BackupRuns.Add(new BackupRun
            {
                CompletedAt = DateTime.Now.AddDays(-1),
                Destination = _db.DestinationDirectory,
                FileName = "countinghouse-2026-01-01-000000.db",
                Succeeded = true,
            });
            await db.SaveChangesAsync();
        }

        Assert.False((await _backups.GetStatusAsync()).ReminderDue);
    }

    [Fact]
    public async Task Status_SurfacesAFailureThatHappenedAfterTheLastSuccess()
    {
        await SeedAsync();
        await _backups.SetDestinationAsync(_db.DestinationDirectory);
        await _backups.CreateBackupAsync();                    // success

        Directory.Delete(_db.DestinationDirectory, recursive: true);
        await File.WriteAllTextAsync(_db.DestinationDirectory, "not a directory");
        await _backups.CreateBackupAsync();                    // fails afterwards

        var status = await _backups.GetStatusAsync();

        // A destination that has quietly stopped working should say why rather than just showing
        // a stale "last backed up" date.
        Assert.NotNull(status.LastError);
        Assert.NotNull(status.LastSuccessAt);
    }

    // ---- rotation, tested directly: the risk here is deleting something we didn't write

    [Fact]
    public void Rotation_KeepsTheNewestAndDeletesTheRest()
    {
        var dir = Path.Combine(_db.Root, "rotate");
        Directory.CreateDirectory(dir);
        for (var day = 1; day <= 10; day++)
            File.WriteAllText(Path.Combine(dir, $"countinghouse-2026-01-{day:00}-120000.db"), "x");

        DatabaseBackupService.RotateDirectory(dir, retain: 3);

        var left = Directory.GetFiles(dir).Select(Path.GetFileName).OrderDescending().ToList();
        Assert.Equal(3, left.Count);
        Assert.Equal("countinghouse-2026-01-10-120000.db", left[0]);
        Assert.Equal("countinghouse-2026-01-08-120000.db", left[2]);
    }

    [Fact]
    public void Rotation_NeverDeletesFilesItDidNotCreate()
    {
        var dir = Path.Combine(_db.Root, "shared");
        Directory.CreateDirectory(dir);
        // The destination sits beside the user's own Quicken backups. Rotation must not
        // touch anything that isn't one of ours, however old.
        var foreign = new[]
        {
            "QDATA-2025-12-06.QDF-backup",
            "countinghouse.db.post-import.bak",
            "countinghouse-notes.db",              // right prefix, wrong shape
            "countinghouse-2026-1-1-120000.db",    // unpadded, not a name we generate
            "important.db",
        };
        foreach (var name in foreign)
            File.WriteAllText(Path.Combine(dir, name), "x");
        for (var day = 1; day <= 5; day++)
            File.WriteAllText(Path.Combine(dir, $"countinghouse-2026-02-{day:00}-120000.db"), "x");

        DatabaseBackupService.RotateDirectory(dir, retain: 1);

        var left = Directory.GetFiles(dir).Select(Path.GetFileName).ToHashSet();
        foreach (var name in foreign)
            Assert.Contains(name, left);
        Assert.Contains("countinghouse-2026-02-05-120000.db", left);
        Assert.Equal(foreign.Length + 1, left.Count);
    }

    [Fact]
    public void Rotation_OnAMissingDirectoryIsHarmless()
    {
        // Housekeeping must never be the thing that fails a backup that otherwise worked.
        DatabaseBackupService.RotateDirectory(Path.Combine(_db.Root, "does-not-exist"), retain: 5);
    }
}
