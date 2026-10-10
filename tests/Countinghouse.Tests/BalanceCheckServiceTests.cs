using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Entities;
using Countinghouse.SimpleFin;

namespace Countinghouse.Tests;

public class BalanceCheckServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    // Fixed so the 45-day gap boundary is deterministic. EarliestReachableDate(Today) is
    // 2026-07-13, so a register whose newest row predates that has an unreachable gap.
    private static readonly DateOnly Today = new(2026, 8, 26);

    public void Dispose() => _db.Dispose();

    private async Task<Account> SeedSyncedAccountAsync(
        decimal openingBalance = 0m,
        decimal syncedBalance = 0m,
        bool hasSynced = true,
        AccountType accountType = AccountType.Checking,
        bool? syncTransactionsOverride = null,
        bool isClosed = false)
    {
        await using var db = _db.CreateDbContext();
        var connection = new SimpleFinConnection { InstitutionLabel = "Bank", EncryptedAccessUrl = "cipher" };
        db.SimpleFinConnections.Add(connection);
        await db.SaveChangesAsync();

        var account = new Account
        {
            Name = "Checking",
            AccountType = accountType,
            OpeningBalance = openingBalance,
            OpeningBalanceDate = new DateOnly(2026, 1, 1),
            CurrentBalance = syncedBalance,
            BalanceAsOf = hasSynced ? new DateTime(2026, 8, 26, 9, 0, 0) : null,
            SimpleFinConnectionId = connection.Id,
            SimpleFinAccountId = "remote-1",
            SyncTransactionsOverride = syncTransactionsOverride,
            IsClosed = isClosed,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private async Task AddRowAsync(int accountId, decimal amount, DateOnly postedDate, bool pending = false)
    {
        await using var db = _db.CreateDbContext();
        db.Transactions.Add(new Transaction
        {
            AccountId = accountId,
            Amount = amount,
            PostedDate = postedDate,
            Payee = "row",
            Pending = pending,
            ClearedStatus = pending ? ClearedStatus.Uncleared : ClearedStatus.Cleared,
            Source = TransactionSource.SimpleFin,
            ExternalId = Guid.NewGuid().ToString(),
        });
        await db.SaveChangesAsync();
    }

    private async Task<AccountBalanceCheck> CheckAsync(int accountId)
    {
        await using var db = _db.CreateDbContext();
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).ToListAsync();
        var results = await BalanceCheckService.CheckAsync(db, accounts, Today);
        return results[0];
    }

    [Fact]
    public async Task Balanced_WhenRegisterSumsToSyncedBalance()
    {
        var account = await SeedSyncedAccountAsync(openingBalance: 100m, syncedBalance: 75m);
        await AddRowAsync(account.Id, -50m, new DateOnly(2026, 8, 20));
        await AddRowAsync(account.Id, 25m, new DateOnly(2026, 8, 21));

        var check = await CheckAsync(account.Id);

        Assert.Equal(BalanceCheckStatus.Balanced, check.Status);
        Assert.Equal(0m, check.Difference);
        Assert.Equal(75m, check.RegisterBalance);
        Assert.Equal(75m, check.SyncedBalance);
        Assert.Null(check.Note);
    }

    // The whole reason this exists: a dropped row leaves the balance correct (it comes straight
    // from the API) and only the register short. Positive difference = register is missing money.
    [Fact]
    public async Task Off_WhenARowIsMissingFromTheRegister()
    {
        var account = await SeedSyncedAccountAsync(openingBalance: 0m, syncedBalance: 500m);
        await AddRowAsync(account.Id, 300m, new DateOnly(2026, 8, 20));

        var check = await CheckAsync(account.Id);

        Assert.Equal(BalanceCheckStatus.Off, check.Status);
        Assert.Equal(200m, check.Difference);
        Assert.Equal(300m, check.RegisterBalance);
    }

    // A register holding more than the bank can't be a dropped inflow. Some institutions subtract
    // scheduled outgoing payments from the balance they publish without listing them, which produces
    // exactly this, so the note has to point there before anyone hunts for a lost row.
    [Fact]
    public async Task Off_NegativeDifference_PointsAtScheduledPayments()
    {
        var account = await SeedSyncedAccountAsync(openingBalance: 0m, syncedBalance: 100m);
        await AddRowAsync(account.Id, 250m, new DateOnly(2026, 8, 20));

        var check = await CheckAsync(account.Id);

        Assert.Equal(BalanceCheckStatus.Off, check.Status);
        Assert.Equal(-150m, check.Difference);
        Assert.Contains("scheduled", check.Note);
    }

    // The gap is the more specific explanation and must win over the generic one.
    [Fact]
    public async Task NegativeDifference_WithAnUnreachableGap_StillNamesTheGap()
    {
        var account = await SeedSyncedAccountAsync(openingBalance: 0m, syncedBalance: 100m);
        await AddRowAsync(account.Id, 250m, new DateOnly(2026, 5, 1));

        var check = await CheckAsync(account.Id);

        Assert.Equal(BalanceCheckStatus.Off, check.Status);
        Assert.Contains("QIF", check.Note);
    }

    // SimpleFIN's `balance` is the posted balance, so a pending row must not count against it.
    // Keyed off Transaction.Pending rather than ClearedStatus on purpose — see the next test.
    [Fact]
    public async Task PendingRows_AreExcludedAndCounted()
    {
        var account = await SeedSyncedAccountAsync(openingBalance: 0m, syncedBalance: 300m);
        await AddRowAsync(account.Id, 300m, new DateOnly(2026, 8, 20));
        await AddRowAsync(account.Id, -80m, new DateOnly(2026, 8, 25), pending: true);

        var check = await CheckAsync(account.Id);

        Assert.Equal(BalanceCheckStatus.Balanced, check.Status);
        Assert.Equal(300m, check.RegisterBalance);
        Assert.Equal(1, check.PendingCount);
    }

    // A row unticked by hand during an open reconciliation is still posted. Testing against
    // ClearedStatus instead of Pending would turn every open session into a spurious "off by $X".
    [Fact]
    public async Task UnticklingARowDuringReconciliation_DoesNotBreakTheCheck()
    {
        var account = await SeedSyncedAccountAsync(openingBalance: 0m, syncedBalance: 300m);
        await AddRowAsync(account.Id, 300m, new DateOnly(2026, 8, 20));

        await using (var db = _db.CreateDbContext())
        {
            var row = await db.Transactions.FirstAsync(t => t.AccountId == account.Id);
            row.ClearedStatus = ClearedStatus.Uncleared;
            await db.SaveChangesAsync();
        }

        var check = await CheckAsync(account.Id);

        Assert.Equal(BalanceCheckStatus.Balanced, check.Status);
    }

    [Fact]
    public async Task ReconciledRows_CountTowardTheRegister()
    {
        var account = await SeedSyncedAccountAsync(openingBalance: 0m, syncedBalance: 300m);
        await AddRowAsync(account.Id, 300m, new DateOnly(2026, 8, 20));

        await using (var db = _db.CreateDbContext())
        {
            var row = await db.Transactions.FirstAsync(t => t.AccountId == account.Id);
            row.ClearedStatus = ClearedStatus.Reconciled;
            await db.SaveChangesAsync();
        }

        var check = await CheckAsync(account.Id);

        Assert.Equal(BalanceCheckStatus.Balanced, check.Status);
    }

    [Fact]
    public async Task NotApplicable_ForAManualAccount()
    {
        await using var db = _db.CreateDbContext();
        var account = new Account
        {
            Name = "Mortgage",
            AccountType = AccountType.Mortgage,
            OpeningBalance = -100_000m,
            CurrentBalance = -95_000m,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var check = await CheckAsync(account.Id);

        Assert.Equal(BalanceCheckStatus.NotApplicable, check.Status);
        Assert.False(check.Applies);
        Assert.Contains("Not synced", check.Note);
    }

    [Fact]
    public async Task NotApplicable_ForABalanceOnlyAccount()
    {
        var account = await SeedSyncedAccountAsync(
            syncedBalance: 42_000m, accountType: AccountType.Investment);

        var check = await CheckAsync(account.Id);

        Assert.Equal(BalanceCheckStatus.NotApplicable, check.Status);
        Assert.Contains("balance only", check.Note);
    }

    // The per-account override wins over the type, same as everywhere else.
    [Fact]
    public async Task Applies_ToAnInvestmentAccountOverriddenToSyncTransactions()
    {
        var account = await SeedSyncedAccountAsync(
            openingBalance: 0m,
            syncedBalance: 10m,
            accountType: AccountType.Investment,
            syncTransactionsOverride: true);
        await AddRowAsync(account.Id, 10m, new DateOnly(2026, 8, 20));

        var check = await CheckAsync(account.Id);

        Assert.Equal(BalanceCheckStatus.Balanced, check.Status);
    }

    [Fact]
    public async Task NotApplicable_WhenTheAccountHasNeverSynced()
    {
        var account = await SeedSyncedAccountAsync(syncedBalance: 500m, hasSynced: false);

        var check = await CheckAsync(account.Id);

        Assert.Equal(BalanceCheckStatus.NotApplicable, check.Status);
        Assert.Contains("never synced", check.Note);
    }

    // A gap explains a difference; it does not suppress the check. An account whose history
    // stopped months ago but still sums exactly has proved nothing was dropped.
    [Fact]
    public async Task StaleHistory_ThatStillBalances_ReportsBalancedWithNoNote()
    {
        var account = await SeedSyncedAccountAsync(openingBalance: 0m, syncedBalance: 300m);
        await AddRowAsync(account.Id, 300m, new DateOnly(2026, 5, 1));

        var check = await CheckAsync(account.Id);

        Assert.Equal(BalanceCheckStatus.Balanced, check.Status);
        Assert.Null(check.Note);
    }

    [Fact]
    public async Task StaleHistory_ThatIsOff_NamesTheUnreachableGap()
    {
        var account = await SeedSyncedAccountAsync(openingBalance: 0m, syncedBalance: 500m);
        await AddRowAsync(account.Id, 300m, new DateOnly(2026, 5, 1));

        var check = await CheckAsync(account.Id);

        Assert.Equal(BalanceCheckStatus.Off, check.Status);
        Assert.Equal(200m, check.Difference);
        Assert.Contains("5/2/2026", check.Note);
        Assert.Contains("7/12/2026", check.Note);
        Assert.Contains("QIF", check.Note);
    }

    // Recent history and an unexplained difference is precisely the case worth looking at, so it
    // must not acquire a reassuring note.
    [Fact]
    public async Task RecentHistory_ThatIsOff_HasNoExplanation()
    {
        var account = await SeedSyncedAccountAsync(openingBalance: 0m, syncedBalance: 500m);
        await AddRowAsync(account.Id, 300m, new DateOnly(2026, 8, 25));

        var check = await CheckAsync(account.Id);

        Assert.Equal(BalanceCheckStatus.Off, check.Status);
        Assert.Null(check.Note);
    }

    [Fact]
    public async Task EmptyRegister_SaysSoRatherThanNamingAGap()
    {
        var account = await SeedSyncedAccountAsync(openingBalance: 0m, syncedBalance: 500m);

        var check = await CheckAsync(account.Id);

        Assert.Equal(BalanceCheckStatus.Off, check.Status);
        Assert.Contains("register is empty", check.Note);
    }

    [Fact]
    public async Task CheckAllAsync_SkipsClosedAccounts()
    {
        await SeedSyncedAccountAsync(syncedBalance: 10m, isClosed: true);
        var open = await SeedSyncedAccountAsync(syncedBalance: 0m);

        var service = new BalanceCheckService(_db);
        var results = await service.CheckAllAsync();

        Assert.Equal(open.Id, Assert.Single(results).AccountId);
    }

    [Fact]
    public async Task CheckAccountAsync_ReturnsNullForAnUnknownAccount()
    {
        var service = new BalanceCheckService(_db);
        Assert.Null(await service.CheckAccountAsync(9999));
    }

    // Amounts are summed in memory because SQLite stores decimal as TEXT; a cent must survive.
    [Fact]
    public async Task Difference_IsExactToTheCent()
    {
        var account = await SeedSyncedAccountAsync(openingBalance: 0m, syncedBalance: 100.00m);
        await AddRowAsync(account.Id, 99.99m, new DateOnly(2026, 8, 20));

        var check = await CheckAsync(account.Id);

        Assert.Equal(BalanceCheckStatus.Off, check.Status);
        Assert.Equal(0.01m, check.Difference);
    }
}
