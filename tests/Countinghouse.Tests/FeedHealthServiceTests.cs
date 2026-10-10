using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Entities;
using Countinghouse.SimpleFin;

namespace Countinghouse.Tests;

public class FeedHealthServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    // A Sunday. Fixed so the weekday arithmetic the MarketDays expectation depends on is deterministic.
    private static readonly DateOnly Today = new(2026, 9, 6);

    public void Dispose() => _db.Dispose();

    private async Task<Account> SeedAsync(
        DateTime? balanceAsOf = null,
        DateTime? lastSyncAt = null,
        string? lastSyncStatus = "Success",
        string? lastSyncError = null,
        string? accountSyncError = null,
        bool synced = true,
        decimal currentBalance = 100m,
        MovementExpectation expectation = MovementExpectation.None,
        int? expectationDays = null,
        AccountType accountType = AccountType.Checking)
    {
        await using var db = _db.CreateDbContext();
        int? connectionId = null;
        if (synced)
        {
            var connection = new SimpleFinConnection
            {
                InstitutionLabel = "Bank",
                EncryptedAccessUrl = "cipher",
                LastSyncAt = lastSyncAt,
                LastSyncStatus = lastSyncStatus,
                LastSyncErrorMessage = lastSyncError,
            };
            db.SimpleFinConnections.Add(connection);
            await db.SaveChangesAsync();
            connectionId = connection.Id;
        }

        var account = new Account
        {
            Name = "Account",
            AccountType = accountType,
            CurrentBalance = currentBalance,
            BalanceAsOf = balanceAsOf,
            SimpleFinConnectionId = connectionId,
            SimpleFinAccountId = synced ? "remote-1" : null,
            LastSyncError = accountSyncError,
            ExpectedMovement = expectation,
            ExpectedMovementDays = expectationDays,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private async Task AddSnapshotsAsync(int accountId, params (string Date, decimal Balance)[] rows)
    {
        await using var db = _db.CreateDbContext();
        foreach (var (date, balance) in rows)
        {
            db.AccountBalanceSnapshots.Add(new AccountBalanceSnapshot
            {
                AccountId = accountId,
                AsOfDate = DateOnly.Parse(date),
                Balance = balance,
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task<AccountFeedHealth> CheckAsync(int accountId)
    {
        await using var db = _db.CreateDbContext();
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).ToListAsync();
        var results = await FeedHealthService.CheckAsync(db, accounts, Today);
        return results[0];
    }

    [Fact]
    public async Task NotApplicable_WhenTheAccountIsNotSynced()
    {
        var account = await SeedAsync(synced: false, balanceAsOf: new DateTime(2026, 9, 6));

        var health = await CheckAsync(account.Id);

        Assert.Equal(FeedHealthStatus.NotApplicable, health.Status);
        Assert.False(health.Applies);
    }

    [Fact]
    public async Task NotApplicable_WhenTheAccountHasNeverSynced()
    {
        var account = await SeedAsync(balanceAsOf: null, lastSyncAt: new DateTime(2026, 9, 6));

        var health = await CheckAsync(account.Id);

        Assert.Equal(FeedHealthStatus.NotApplicable, health.Status);
    }

    // One Access URL spans several institutions. A failure at one of them must not paint every
    // other account on the connection as failing.
    [Fact]
    public async Task PartialSync_FailsOnlyTheAccountTheErrorWasPinnedOn()
    {
        var blamed = await SeedAsync(
            balanceAsOf: new DateTime(2026, 9, 6),
            lastSyncAt: new DateTime(2026, 9, 6),
            lastSyncStatus: "PartialSuccess",
            lastSyncError: "Connection to Card Issuer may need attention.",
            accountSyncError: "Connection to Card Issuer may need attention.");

        var health = await CheckAsync(blamed.Id);

        Assert.Equal(FeedHealthStatus.SyncFailing, health.Status);
        Assert.Contains("Card Issuer", health.Note);
    }

    [Fact]
    public async Task PartialSync_LeavesAnUnblamedAccountToBeJudgedOnItsOwn()
    {
        var current = await SeedAsync(
            balanceAsOf: new DateTime(2026, 9, 6),
            lastSyncAt: new DateTime(2026, 9, 6),
            lastSyncStatus: "PartialSuccess",
            lastSyncError: "Connection to Card Issuer may need attention.");

        Assert.Equal(FeedHealthStatus.Ok, (await CheckAsync(current.Id)).Status);
    }

    [Fact]
    public async Task PartialSync_StillCatchesAFrozenBalanceOnAnUnblamedAccount()
    {
        var frozen = await SeedAsync(
            balanceAsOf: new DateTime(2026, 8, 1),
            lastSyncAt: new DateTime(2026, 9, 6),
            lastSyncStatus: "PartialSuccess",
            lastSyncError: "Connection to Card Issuer may need attention.");

        var health = await CheckAsync(frozen.Id);

        Assert.Equal(FeedHealthStatus.Stale, health.Status);
        Assert.Equal(FeedStaleKind.BalanceNotAdvancing, health.Kind);
    }

    // The connection's own error is more specific than anything this service could infer, so a
    // reported failure wins outright rather than being buried under a derived complaint.
    [Fact]
    public async Task SyncFailing_TakesPrecedenceOverStaleness()
    {
        var account = await SeedAsync(
            balanceAsOf: new DateTime(2026, 8, 1),
            lastSyncAt: new DateTime(2026, 9, 6),
            lastSyncStatus: "Failed",
            lastSyncError: "Re-link this institution.");

        var health = await CheckAsync(account.Id);

        Assert.Equal(FeedHealthStatus.SyncFailing, health.Status);
        Assert.Null(health.Kind);
        Assert.Contains("Re-link this institution.", health.Note);
    }

    // The failure this whole service exists for: a feed can freeze while syncs keep recording Success,
    // and the balance check reads a frozen figure as an ordinary difference rather than a dead feed.
    [Fact]
    public async Task BalanceNotAdvancing_WhenTheFeedFreezesUnderASuccessfulSync()
    {
        var account = await SeedAsync(
            balanceAsOf: new DateTime(2026, 9, 2, 8, 45, 0),
            lastSyncAt: new DateTime(2026, 9, 6, 8, 49, 0));

        var health = await CheckAsync(account.Id);

        Assert.Equal(FeedHealthStatus.Stale, health.Status);
        Assert.Equal(FeedStaleKind.BalanceNotAdvancing, health.Kind);
        Assert.Equal(4, health.DaysBehind);
        Assert.Contains("re-authenticate", health.Note!, StringComparison.OrdinalIgnoreCase);
    }

    // Three days is the threshold, and it is inclusive: benign lag can reach two under normal
    // rotation, so firing at three would cry wolf on an account that is actually fine.
    [Fact]
    public async Task Ok_WhenTheLagIsExactlyTheThreshold()
    {
        var account = await SeedAsync(
            balanceAsOf: new DateTime(2026, 9, 3),
            lastSyncAt: new DateTime(2026, 9, 6));

        var health = await CheckAsync(account.Id);

        Assert.Equal(FeedHealthStatus.Ok, health.Status);
    }

    // Lag is measured to the last sync, never to today. Syncs here are irregular by design, and an
    // account nobody has asked about for a month has not gone stale — it has gone unasked.
    [Fact]
    public async Task Ok_WhenTheBalanceMatchedTheLastSyncHoweverLongAgoThatWas()
    {
        var account = await SeedAsync(
            balanceAsOf: new DateTime(2026, 8, 1),
            lastSyncAt: new DateTime(2026, 8, 1));

        var health = await CheckAsync(account.Id);

        Assert.Equal(FeedHealthStatus.Ok, health.Status);
        Assert.Equal(0, health.DaysBehind);
    }

    [Fact]
    public async Task Unknown_WhenTheConnectionHasNeverRecordedASync()
    {
        var account = await SeedAsync(balanceAsOf: new DateTime(2026, 9, 6), lastSyncAt: null);

        var health = await CheckAsync(account.Id);

        Assert.Equal(FeedHealthStatus.Unknown, health.Status);
    }

    // An account with no expectation set can sit at one figure forever without that meaning
    // anything — e.g. a paid-off card sitting at $0.00 for its entire snapshot series.
    [Fact]
    public async Task Ok_WhenThereIsNoExpectationHoweverFlatTheBalance()
    {
        var account = await SeedAsync(
            balanceAsOf: new DateTime(2026, 9, 6),
            lastSyncAt: new DateTime(2026, 9, 6));
        await AddSnapshotsAsync(account.Id,
            ("2026-06-01", 100m), ("2026-07-01", 100m), ("2026-09-06", 100m));

        var health = await CheckAsync(account.Id);

        Assert.Equal(FeedHealthStatus.Ok, health.Status);
    }

    [Fact]
    public async Task NoMovement_WhenAWithinDaysExpectationIsExceeded()
    {
        var account = await SeedAsync(
            balanceAsOf: new DateTime(2026, 9, 6),
            lastSyncAt: new DateTime(2026, 9, 6),
            currentBalance: 200.50m,
            expectation: MovementExpectation.WithinDays,
            expectationDays: 35);
        // A savings account whose interest posts monthly and nothing else moves. Flat since July.
        await AddSnapshotsAsync(account.Id,
            ("2026-06-21", 200.00m), ("2026-07-21", 200.50m), ("2026-09-06", 200.50m));

        var health = await CheckAsync(account.Id);

        Assert.Equal(FeedHealthStatus.Stale, health.Status);
        Assert.Equal(FeedStaleKind.NoMovement, health.Kind);
        Assert.Equal(new DateOnly(2026, 7, 21), health.UnchangedSince);
        Assert.Equal(47, health.DaysWithoutMovement);
    }

    [Fact]
    public async Task Ok_WhenAWithinDaysExpectationIsStillInsideItsWindow()
    {
        var account = await SeedAsync(
            balanceAsOf: new DateTime(2026, 9, 6),
            lastSyncAt: new DateTime(2026, 9, 6),
            currentBalance: 200.50m,
            expectation: MovementExpectation.WithinDays,
            expectationDays: 35);
        await AddSnapshotsAsync(account.Id,
            ("2026-07-21", 200.00m), ("2026-08-21", 200.50m), ("2026-09-06", 200.50m));

        var health = await CheckAsync(account.Id);

        Assert.Equal(FeedHealthStatus.Ok, health.Status);
        Assert.Equal(new DateOnly(2026, 8, 21), health.UnchangedSince);
    }

    // A weekend re-serve is not evidence of movement: an investment feed can hand back the prior
    // business day's figure unchanged. Comparing across weekend observations would report "no
    // movement" every weekend on market accounts that are otherwise fine.
    [Fact]
    public async Task MarketDays_IgnoresWeekendObservations()
    {
        var account = await SeedAsync(
            balanceAsOf: new DateTime(2026, 9, 4),
            lastSyncAt: new DateTime(2026, 9, 6),
            currentBalance: 50250.00m,
            expectation: MovementExpectation.MarketDays,
            accountType: AccountType.Investment);
        await AddSnapshotsAsync(account.Id,
            ("2026-08-28", 50000.00m),   // Friday
            ("2026-08-30", 49900.00m),   // Sunday — a stale re-serve, and the newest row
            ("2026-09-04", 50250.00m));  // Friday

        var health = await CheckAsync(account.Id);

        Assert.Equal(FeedHealthStatus.Ok, health.Status);
        Assert.Equal(new DateOnly(2026, 9, 4), health.UnchangedSince);
    }

    [Fact]
    public async Task MarketDays_StaleAfterTooManyTradingDaysWithoutMovement()
    {
        var account = await SeedAsync(
            balanceAsOf: new DateTime(2026, 8, 28),
            lastSyncAt: new DateTime(2026, 8, 28),
            currentBalance: 100000.00m,
            expectation: MovementExpectation.MarketDays,
            accountType: AccountType.Investment);
        // Unchanged from Monday 08-17 through Friday 08-28: ten trading days.
        await AddSnapshotsAsync(account.Id,
            ("2026-08-14", 98000.00m),
            ("2026-08-17", 100000.00m),
            ("2026-08-21", 100000.00m),
            ("2026-08-28", 100000.00m));

        var health = await CheckAsync(account.Id);

        Assert.Equal(FeedHealthStatus.Stale, health.Status);
        Assert.Equal(FeedStaleKind.NoMovement, health.Kind);
        Assert.Equal(new DateOnly(2026, 8, 17), health.UnchangedSince);
        Assert.Equal(9, health.DaysWithoutMovement);
    }

    // "We can't tell" and "it's fine" are different answers, and only one of them is reassuring.
    // A market account synced only at weekends has no usable observations at all.
    [Fact]
    public async Task Unknown_WhenAMarketAccountHasTooFewWeekdayObservations()
    {
        var account = await SeedAsync(
            balanceAsOf: new DateTime(2026, 9, 6),
            lastSyncAt: new DateTime(2026, 9, 6),
            currentBalance: 100m,
            expectation: MovementExpectation.MarketDays,
            accountType: AccountType.Investment);
        await AddSnapshotsAsync(account.Id, ("2026-08-30", 100m), ("2026-09-06", 100m));

        var health = await CheckAsync(account.Id);

        Assert.Equal(FeedHealthStatus.Unknown, health.Status);
        Assert.Contains("weekday", health.Note!);
    }

    // A series that has only ever held one value may be a dormant account rather than a dead feed,
    // and the note has to say so — the difference is not knowable from the snapshots alone.
    [Fact]
    public async Task NoMovement_SaysSoWhenTheSeriesNeverMoved()
    {
        var account = await SeedAsync(
            balanceAsOf: new DateTime(2026, 9, 6),
            lastSyncAt: new DateTime(2026, 9, 6),
            currentBalance: 25.00m,
            expectation: MovementExpectation.WithinDays,
            expectationDays: 30);
        await AddSnapshotsAsync(account.Id,
            ("2026-06-01", 25.00m), ("2026-07-01", 25.00m), ("2026-09-06", 25.00m));

        var health = await CheckAsync(account.Id);

        Assert.Equal(FeedStaleKind.NoMovement, health.Kind);
        Assert.Contains("may predate the snapshot series", health.Note!);
    }

    // A WithinDays expectation with no window is not an expectation and must never fire.
    [Fact]
    public async Task Ok_WhenAWithinDaysExpectationHasNoWindowSet()
    {
        var account = await SeedAsync(
            balanceAsOf: new DateTime(2026, 9, 6),
            lastSyncAt: new DateTime(2026, 9, 6),
            expectation: MovementExpectation.WithinDays,
            expectationDays: null);
        await AddSnapshotsAsync(account.Id, ("2026-01-01", 100m), ("2026-09-06", 100m));

        var health = await CheckAsync(account.Id);

        Assert.Equal(FeedHealthStatus.Ok, health.Status);
    }

    [Theory]
    [InlineData("2026-09-04", "2026-09-06", 0)]  // Fri -> Sun: no trading days in between
    [InlineData("2026-09-04", "2026-09-07", 1)]  // Fri -> Mon
    [InlineData("2026-08-28", "2026-09-04", 5)]  // Fri -> Fri, one weekend skipped
    [InlineData("2026-09-06", "2026-09-06", 0)]
    public void WeekdaysBetween_CountsTradingDaysExclusiveOfTheStart(string from, string to, int expected) =>
        Assert.Equal(expected, FeedHealthService.WeekdaysBetween(DateOnly.Parse(from), DateOnly.Parse(to)));
}
