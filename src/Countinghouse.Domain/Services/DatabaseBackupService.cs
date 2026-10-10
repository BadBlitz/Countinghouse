using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;
using Countinghouse.Domain.Entities;

namespace Countinghouse.Domain.Services;

// Where the live database is and where snapshots are staged. Injected rather than read from
// AppPaths directly so tests can point at a temporary database.
public sealed record BackupPaths(string SourceDbPath, string StagingDirectory);

public sealed record BackupStatus
{
    public string? Destination { get; init; }
    public DateTime? LastSuccessAt { get; init; }
    public int? DaysSinceLastSuccess { get; init; }

    // True when there's no successful backup in the reminder window. Drives the dashboard nudge.
    public bool ReminderDue { get; init; }
    public bool DestinationConfigured => !string.IsNullOrWhiteSpace(Destination);

    // Error from the most recent failure since the last success, if any — so a NAS that's been
    // unreachable for a week says why rather than just going quiet.
    public string? LastError { get; init; }
    public List<BackupRun> Recent { get; init; } = [];
}

public sealed record BackupOutcome(bool Succeeded, string? Error, BackupRun? Run)
{
    // Set when the snapshot was taken and verified locally but couldn't reach the destination —
    // a partial success worth reporting differently from an outright failure.
    public bool LocalCopyKept { get; init; }
}

public class DatabaseBackupService(IDbContextFactory<CountinghouseContext> factory, BackupPaths paths)
{
    public const string DestinationSettingKey = "BackupDestination";
    public const int ReminderAfterDays = 7;

    // Generous: these snapshots are small next to the Quicken backup files that may already
    // share the same directory.
    private const int RetainAtDestination = 20;
    private const int RetainLocally = 5;

    // Deliberately strict, and only ever applied inside a directory this app creates, so rotation
    // can never touch a file it didn't write (e.g. the user's own Quicken .QDF-backup files).
    private static readonly Regex BackupFileName =
        new(@"^countinghouse-\d{4}-\d{2}-\d{2}-\d{6}\.db$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<string?> GetDestinationAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var setting = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == DestinationSettingKey);
        return string.IsNullOrWhiteSpace(setting?.Value) ? null : setting.Value;
    }

    // Returns an error message, or null on success. Verified by actually creating the directory:
    // a path that can't be written to is not a backup destination, and finding that out now beats
    // finding out when a backup is needed.
    public async Task<string?> SetDestinationAsync(string destination)
    {
        destination = destination.Trim();
        if (destination.Length == 0)
            return "Enter a folder path.";

        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex)
        {
            return $"Can't use that folder: {ex.Message}";
        }

        await using var db = await factory.CreateDbContextAsync();
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == DestinationSettingKey);
        if (setting is null)
            db.AppSettings.Add(new AppSetting { Key = DestinationSettingKey, Value = destination });
        else
            setting.Value = destination;
        await db.SaveChangesAsync();
        return null;
    }

    public async Task<BackupStatus> GetStatusAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var recent = await db.BackupRuns.AsNoTracking()
            .OrderByDescending(r => r.CompletedAt)
            .Take(10)
            .ToListAsync();

        var lastSuccess = recent.FirstOrDefault(r => r.Succeeded)?.CompletedAt
            ?? await db.BackupRuns.AsNoTracking()
                .Where(r => r.Succeeded)
                .OrderByDescending(r => r.CompletedAt)
                .Select(r => (DateTime?)r.CompletedAt)
                .FirstOrDefaultAsync();

        var days = lastSuccess is null ? null : (int?)(DateTime.Now - lastSuccess.Value).TotalDays;

        return new BackupStatus
        {
            Destination = await GetDestinationAsync(),
            LastSuccessAt = lastSuccess,
            DaysSinceLastSuccess = days,
            ReminderDue = lastSuccess is null || days >= ReminderAfterDays,
            LastError = recent.FirstOrDefault(r => !r.Succeeded && r.CompletedAt > (lastSuccess ?? DateTime.MinValue))?.ErrorMessage,
            Recent = recent,
        };
    }

    // Snapshot locally with VACUUM INTO, verify it, then copy out. Going through a local staging
    // file rather than writing SQLite output straight to a network share keeps SQLite off the
    // network entirely and means a dropped connection can't leave a half-written snapshot behind.
    public async Task<BackupOutcome> CreateBackupAsync()
    {
        var destination = await GetDestinationAsync();
        var fileName = $"countinghouse-{DateTime.Now:yyyy-MM-dd-HHmmss}.db";
        Directory.CreateDirectory(paths.StagingDirectory);
        var stagingPath = Path.Combine(paths.StagingDirectory, fileName);

        int accounts;
        int transactions;
        long size;
        try
        {
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);

            await VacuumIntoAsync(stagingPath);
            (accounts, transactions) = await VerifySnapshotAsync(stagingPath);
            size = new FileInfo(stagingPath).Length;
        }
        catch (Exception ex)
        {
            // A snapshot that can't even be taken locally is a hard failure; log it so the
            // reminder keeps nagging.
            await RecordAsync(new BackupRun
            {
                CompletedAt = DateTime.Now,
                Destination = destination ?? "",
                FileName = fileName,
                Succeeded = false,
                ErrorMessage = $"Couldn't create the snapshot: {ex.Message}",
            });
            return new BackupOutcome(false, $"Couldn't create the snapshot: {ex.Message}", null);
        }

        RotateDirectory(paths.StagingDirectory, RetainLocally);

        if (destination is null)
        {
            var run = await RecordAsync(new BackupRun
            {
                CompletedAt = DateTime.Now,
                Destination = "",
                FileName = fileName,
                SizeBytes = size,
                AccountCount = accounts,
                TransactionCount = transactions,
                Succeeded = false,
                ErrorMessage = "No backup destination is set, so the snapshot was kept locally only.",
            });
            return new BackupOutcome(false, run.ErrorMessage, run) { LocalCopyKept = true };
        }

        try
        {
            Directory.CreateDirectory(destination);
            var destinationPath = Path.Combine(destination, fileName);
            File.Copy(stagingPath, destinationPath, overwrite: true);

            // Cheap end-to-end check that the bytes actually landed: a share that silently
            // truncates is worse than one that throws.
            var copiedSize = new FileInfo(destinationPath).Length;
            if (copiedSize != size)
                throw new IOException($"copied {copiedSize:N0} bytes but the snapshot is {size:N0}");

            RotateDirectory(destination, RetainAtDestination);

            var run = await RecordAsync(new BackupRun
            {
                CompletedAt = DateTime.Now,
                Destination = destination,
                FileName = fileName,
                SizeBytes = size,
                AccountCount = accounts,
                TransactionCount = transactions,
                Succeeded = true,
            });
            return new BackupOutcome(true, null, run) { LocalCopyKept = true };
        }
        catch (Exception ex)
        {
            // The usual case here is the NAS being asleep or off-network. The local snapshot is
            // already taken and verified, so this is a partial success, not a lost backup.
            var run = await RecordAsync(new BackupRun
            {
                CompletedAt = DateTime.Now,
                Destination = destination,
                FileName = fileName,
                SizeBytes = size,
                AccountCount = accounts,
                TransactionCount = transactions,
                Succeeded = false,
                ErrorMessage = $"Snapshot taken locally, but copying to {destination} failed: {ex.Message}",
            });
            return new BackupOutcome(false, run.ErrorMessage, run) { LocalCopyKept = true };
        }
    }

    private async Task VacuumIntoAsync(string destinationPath)
    {
        // A raw connection rather than the DbContext: VACUUM can't run inside a transaction, and
        // this keeps EF's command semantics out of it.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.SourceDbPath,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // VACUUM INTO won't take a parameter for the filename.
        command.CommandText = $"VACUUM INTO '{destinationPath.Replace("'", "''")}';";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(int Accounts, int Transactions)> VerifySnapshotAsync(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check;";
            var result = (await check.ExecuteScalarAsync())?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"integrity_check returned \"{result}\"");
        }

        return (await CountAsync(connection, "Accounts"), await CountAsync(connection, "Transactions"));

        static async Task<int> CountAsync(SqliteConnection connection, string table)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }
    }

    // Deletes only files whose names this service generated, so an unexpected directory (or one
    // shared with other backups) can't lose anything. Internal so the deletion-safety property
    // can be tested directly instead of by generating enough backups to trigger it.
    internal static void RotateDirectory(string directory, int retain)
    {
        try
        {
            var ours = new DirectoryInfo(directory).GetFiles("countinghouse-*.db")
                .Where(f => BackupFileName.IsMatch(f.Name))
                .OrderByDescending(f => f.Name)
                .Skip(retain)
                .ToList();
            foreach (var file in ours)
                file.Delete();
        }
        catch
        {
            // Rotation is housekeeping. Failing to prune must never fail a backup that succeeded.
        }
    }

    private async Task<BackupRun> RecordAsync(BackupRun run)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.BackupRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }
}
