using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Entities;
using Countinghouse.Domain.Services;

namespace Countinghouse.Tests;

public class BalanceSnapshotWriterTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AccountService _accounts;
    private readonly TransactionService _transactions;

    public BalanceSnapshotWriterTests()
    {
        _accounts = new AccountService(_db);
        _transactions = new TransactionService(_db);
    }

    public void Dispose() => _db.Dispose();

    private async Task<Account> CreateAccountAsync(
        decimal opening = 1000m,
        AccountType type = AccountType.Checking,
        string name = "Checking",
        bool isClosed = false)
    {
        var account = await _accounts.CreateAccountAsync(new Account
        {
            Name = name,
            AccountType = type,
            OpeningBalance = opening,
        });

        if (isClosed)
        {
            await using var db = _db.CreateDbContext();
            var tracked = await db.Accounts.FirstAsync(a => a.Id == account.Id);
            tracked.IsClosed = true;
            await db.SaveChangesAsync();
        }

        return account;
    }

    private async Task<List<AccountBalanceSnapshot>> SnapshotsAsync(int accountId)
    {
        await using var db = _db.CreateDbContext();
        return await db.AccountBalanceSnapshots.AsNoTracking()
            .Where(s => s.AccountId == accountId)
            .OrderBy(s => s.AsOfDate)
            .ToListAsync();
    }

    [Fact]
    public async Task RecordAll_WritesOneRowPerOpenAccount()
    {
        var checking = await CreateAccountAsync(name: "Checking");
        var savings = await CreateAccountAsync(opening: 250m, name: "Savings", type: AccountType.Savings);

        await using (var db = _db.CreateDbContext())
        {
            var count = await BalanceSnapshotWriter.RecordAllAsync(db);
            Assert.Equal(2, count);
        }

        Assert.Equal(1000m, Assert.Single(await SnapshotsAsync(checking.Id)).Balance);
        Assert.Equal(250m, Assert.Single(await SnapshotsAsync(savings.Id)).Balance);
    }

    [Fact]
    public async Task RecordAll_SkipsClosedAccounts()
    {
        var open = await CreateAccountAsync(name: "Open");
        var closed = await CreateAccountAsync(name: "Closed", isClosed: true);

        await using (var db = _db.CreateDbContext())
            Assert.Equal(1, await BalanceSnapshotWriter.RecordAllAsync(db));

        Assert.Single(await SnapshotsAsync(open.Id));
        Assert.Empty(await SnapshotsAsync(closed.Id));
    }

    // The unique index on (AccountId, AsOfDate) would throw on a second insert; the writer has to
    // update in place instead. Several syncs a day is the normal case, not an edge case.
    [Fact]
    public async Task Record_SameDayTwice_UpdatesRatherThanDuplicating()
    {
        var account = await CreateAccountAsync();

        await using (var db = _db.CreateDbContext())
            await BalanceSnapshotWriter.RecordAsync(db, account.Id);

        await _transactions.AddTransactionAsync(new Transaction
        {
            AccountId = account.Id,
            Amount = -125m,
            PostedDate = DateOnly.FromDateTime(DateTime.Today),
            Payee = "Groceries",
            Source = TransactionSource.Manual,
        });

        await using (var db = _db.CreateDbContext())
            await BalanceSnapshotWriter.RecordAsync(db, account.Id);

        // The later balance wins: a day ends on its last figure, not its first.
        var snapshot = Assert.Single(await SnapshotsAsync(account.Id));
        Assert.Equal(875m, snapshot.Balance);
    }

    [Fact]
    public async Task Record_DatesSnapshotFromBalanceAsOf_NotToday()
    {
        var account = await CreateAccountAsync();
        var staleDate = new DateTime(2026, 3, 4, 9, 30, 0);

        await using (var db = _db.CreateDbContext())
        {
            var tracked = await db.Accounts.FirstAsync(a => a.Id == account.Id);
            tracked.BalanceAsOf = staleDate;
            await db.SaveChangesAsync();
            await BalanceSnapshotWriter.RecordAsync(db, tracked);
        }

        var snapshot = Assert.Single(await SnapshotsAsync(account.Id));
        Assert.Equal(new DateOnly(2026, 3, 4), snapshot.AsOfDate);
    }

    // BalanceCalculator is the choke point every manual balance change runs through, so hooking it
    // there is what makes transaction edits, account edits and the mortgage update all covered.
    [Fact]
    public async Task AddingTransaction_RecordsSnapshotThroughBalanceCalculator()
    {
        var account = await CreateAccountAsync();

        await _transactions.AddTransactionAsync(new Transaction
        {
            AccountId = account.Id,
            Amount = -40m,
            PostedDate = DateOnly.FromDateTime(DateTime.Today),
            Payee = "Fuel",
            Source = TransactionSource.Manual,
        });

        var snapshot = Assert.Single(await SnapshotsAsync(account.Id));
        Assert.Equal(960m, snapshot.Balance);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), snapshot.AsOfDate);
    }

    [Fact]
    public async Task UpdateBalance_RecordsSnapshotAtTheNewBalance()
    {
        var account = await CreateAccountAsync(opening: -250000m, type: AccountType.Mortgage, name: "Mortgage");

        await _accounts.UpdateBalanceAsync(account.Id, -248500m, new DateOnly(2026, 2, 1));

        var snapshot = Assert.Single(await SnapshotsAsync(account.Id));
        Assert.Equal(-248500m, snapshot.Balance);
    }

    // Synced accounts take BalanceCalculator's early return, so editing their rows must not write
    // a snapshot — their balance comes from the API and hasn't moved.
    [Fact]
    public async Task SyncedAccount_TransactionEdit_RecordsNoSnapshot()
    {
        var account = await CreateAccountAsync();

        await using (var db = _db.CreateDbContext())
        {
            var connection = new SimpleFinConnection
            {
                InstitutionLabel = "Test Bank",
                EncryptedAccessUrl = "ciphertext",
            };
            db.SimpleFinConnections.Add(connection);
            await db.SaveChangesAsync();

            var tracked = await db.Accounts.FirstAsync(a => a.Id == account.Id);
            tracked.SimpleFinConnectionId = connection.Id;
            tracked.SimpleFinAccountId = "ACT-1";
            await db.SaveChangesAsync();
        }

        await _transactions.AddTransactionAsync(new Transaction
        {
            AccountId = account.Id,
            Amount = -40m,
            PostedDate = DateOnly.FromDateTime(DateTime.Today),
            Payee = "Fuel",
            Source = TransactionSource.Manual,
        });

        Assert.Empty(await SnapshotsAsync(account.Id));
    }
}
