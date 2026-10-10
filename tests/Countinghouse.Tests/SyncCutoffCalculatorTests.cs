using Countinghouse.Domain.Entities;
using Countinghouse.SimpleFin;

namespace Countinghouse.Tests;

public class SyncCutoffCalculatorTests : IDisposable
{
    private readonly TestDb _db = new();

    // Fixed "today" so the 90-day clamp is deterministic. The seeded cutoffs below run
    // 2026-03-23 to 2026-08-07, straddling the clamp.
    private static readonly DateOnly Today = new(2026, 8, 14);

    public void Dispose() => _db.Dispose();

    // Four accounts under one Access URL whose imported history ends on four different
    // days, 2026-03-23 through 2026-08-07.
    private async Task<int> SeedMainBankConnectionAsync()
    {
        await using var db = _db.CreateDbContext();

        var connection = new SimpleFinConnection { InstitutionLabel = "Main Bank", EncryptedAccessUrl = "cipher" };
        db.SimpleFinConnections.Add(connection);
        await db.SaveChangesAsync();

        var lastImported = new (string Name, string RemoteId, DateOnly Last)[]
        {
            ("Main Bank Checking", "bank-1", new DateOnly(2026, 8, 7)),
            ("Main Bank Savings #1", "bank-2", new DateOnly(2026, 8, 7)),
            ("Main Bank Savings #2", "bank-3", new DateOnly(2026, 7, 13)),
            ("Main Bank Credit Card", "bank-4", new DateOnly(2026, 3, 23)),
        };

        var batchId = Guid.NewGuid();
        foreach (var (name, remoteId, last) in lastImported)
        {
            var account = new Account
            {
                Name = name,
                AccountType = AccountType.Checking,
                SimpleFinConnectionId = connection.Id,
                SimpleFinAccountId = remoteId,
                OpeningBalanceDate = new DateOnly(2013, 1, 1),
            };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();

            db.Transactions.AddRange(
                new Transaction
                {
                    AccountId = account.Id, Amount = -10m, Payee = "older row",
                    PostedDate = last.AddDays(-30), ImportBatchId = batchId,
                },
                new Transaction
                {
                    AccountId = account.Id, Amount = -20m, Payee = "last imported row",
                    PostedDate = last, ImportBatchId = batchId,
                });
        }

        await db.SaveChangesAsync();
        return connection.Id;
    }

    [Fact]
    public async Task EachAccountGetsItsOwnLastImportedDate()
    {
        var connectionId = await SeedMainBankConnectionAsync();
        await using var db = _db.CreateDbContext();

        var cutoffs = await SyncCutoffCalculator.ForConnectionAsync(db, connectionId);

        Assert.Equal(4, cutoffs.Count);
        Assert.Equal(new DateOnly(2026, 8, 7), Single(cutoffs, "bank-1").LastImportedDate);
        Assert.Equal(new DateOnly(2026, 7, 13), Single(cutoffs, "bank-3").LastImportedDate);
        Assert.Equal(new DateOnly(2026, 3, 23), Single(cutoffs, "bank-4").LastImportedDate);
    }

    [Fact]
    public async Task RequestNeverNarrowsToTheLatestAccountsCutoff()
    {
        var connectionId = await SeedMainBankConnectionAsync();
        await using var db = _db.CreateDbContext();

        var cutoffs = await SyncCutoffCalculator.ForConnectionAsync(db, connectionId);
        var requested = SyncCutoffCalculator.EarliestRequestDate(cutoffs, Today);

        // The whole hazard this class exists for: one start-date serves four accounts whose
        // history ends on four different days, so anchoring to the newest (2026-08-07) would
        // silently drop everything the other three are missing.
        Assert.True(requested < new DateOnly(2026, 8, 7));
        Assert.Equal(SyncCutoffCalculator.EarliestReachableDate(Today), requested);
    }

    [Fact]
    public async Task RequestIsClampedToWhatTheServerWillActuallyServe()
    {
        var connectionId = await SeedMainBankConnectionAsync();
        await using var db = _db.CreateDbContext();

        var cutoffs = await SyncCutoffCalculator.ForConnectionAsync(db, connectionId);

        // Unclamped this would ask for 2026-03-24, which draws a "partial sync" warning on every
        // single run while saying nothing about that run.
        //
        // 07-01 is 44 days back, not 45: a midnight start-date exactly at the limit still
        // measures as over it once the current time of day is counted.
        Assert.Equal(
            new DateOnly(2026, 7, 1),
            SyncCutoffCalculator.EarliestRequestDate(cutoffs, Today));
    }

    [Fact]
    public async Task HistoryEndingBeforeTheServableWindowIsReportedAsAnUnreachableGap()
    {
        var connectionId = await SeedMainBankConnectionAsync();
        await using var db = _db.CreateDbContext();

        var cutoffs = await SyncCutoffCalculator.ForConnectionAsync(db, connectionId);

        // The credit card's history stops 2026-03-23 but nothing before 2026-07-01 is fetched,
        // so those months are reachable only via a fresh QIF export.
        var gap = Single(cutoffs, "bank-4").UnreachableGap(Today);
        Assert.NotNull(gap);
        Assert.Equal(new DateOnly(2026, 3, 24), gap!.Value.From);
        Assert.Equal(new DateOnly(2026, 6, 30), gap.Value.To);

        // The accounts whose history reaches into the window have nothing missing.
        Assert.Null(Single(cutoffs, "bank-1").UnreachableGap(Today));
        Assert.Null(Single(cutoffs, "bank-3").UnreachableGap(Today));
    }

    [Fact]
    public async Task SyncedRowsCloseTheGapEvenThoughTheImportBoundaryNeverMoves()
    {
        // The import boundary is frozen at 2026-03-23 forever. Six weeks later that is outside
        // the lookback window, so measuring coverage from it would report a permanent hole in an
        // account that has in fact been synced every day since.
        var connectionId = await SeedMainBankConnectionAsync();

        await using (var seed = _db.CreateDbContext())
        {
            var card = seed.Accounts.First(a => a.SimpleFinAccountId == "bank-4");
            seed.Transactions.Add(new Transaction
            {
                AccountId = card.Id, Amount = -3m, Payee = "synced yesterday",
                PostedDate = Today.AddDays(-1),
                Source = TransactionSource.SimpleFin, ExternalId = "TXN-recent",
            });
            await seed.SaveChangesAsync();
        }

        await using var db = _db.CreateDbContext();
        var card2 = Single(await SyncCutoffCalculator.ForConnectionAsync(db, connectionId), "bank-4");

        // Discarding still keys off the frozen import boundary — that part must not move.
        Assert.Equal(new DateOnly(2026, 3, 23), card2.LastImportedDate);
        Assert.True(card2.Discards(new DateOnly(2026, 3, 23)));
        // But coverage is up to date, so there is nothing to warn about.
        Assert.Equal(Today.AddDays(-1), card2.LastKnownDate);
        Assert.Null(card2.UnreachableGap(Today));
    }

    [Fact]
    public async Task ABalanceOnlyAccountDoesNotDragTheWindowOpenForever()
    {
        // An investment account's transactions are discarded on arrival, so treating it as
        // "needs a first sync" would request 90 days of data we throw away, on every sync,
        // in exchange for a standing "exceeds recommended range" warning.
        var connectionId = await SeedMainBankConnectionAsync();

        await using (var seed = _db.CreateDbContext())
        {
            seed.Accounts.Add(new Account
            {
                Name = "Brokerage IRA", AccountType = AccountType.Investment,
                SimpleFinConnectionId = connectionId, SimpleFinAccountId = "fid-1",
            });
            await seed.SaveChangesAsync();
        }

        await using var db = _db.CreateDbContext();
        var cutoffs = await SyncCutoffCalculator.ForConnectionAsync(db, connectionId);

        Assert.False(Single(cutoffs, "fid-1").SyncsTransactions);
        Assert.Equal(
            SyncCutoffCalculator.EarliestReachableDate(Today),
            SyncCutoffCalculator.EarliestRequestDate(cutoffs, Today));
    }

    [Fact]
    public async Task AnAccountWithNoImportedHistoryHasNoGapToReport()
    {
        var connectionId = await SeedMainBankConnectionAsync();

        await using (var seed = _db.CreateDbContext())
        {
            seed.Accounts.Add(new Account
            {
                Name = "Main Bank Newly Opened", AccountType = AccountType.Savings,
                SimpleFinConnectionId = connectionId, SimpleFinAccountId = "bank-new",
            });
            await seed.SaveChangesAsync();
        }

        await using var db = _db.CreateDbContext();
        var cutoffs = await SyncCutoffCalculator.ForConnectionAsync(db, connectionId);

        // Nothing was imported, so there is no boundary for anything to fall short of.
        Assert.Null(Single(cutoffs, "bank-new").UnreachableGap(Today));
    }

    [Fact]
    public async Task TheWidenedWindowDoesNotDuplicateRowsTheOtherAccountsAlreadyHave()
    {
        var connectionId = await SeedMainBankConnectionAsync();
        await using var db = _db.CreateDbContext();

        var checking = Single(await SyncCutoffCalculator.ForConnectionAsync(db, connectionId), "bank-1");

        // Everything the March-cutoff account needed is old news to this one.
        Assert.True(checking.Discards(new DateOnly(2026, 4, 1)));
        Assert.True(checking.Discards(new DateOnly(2026, 8, 7)));   // boundary: "on or before"
        Assert.False(checking.Discards(new DateOnly(2026, 8, 8)));
    }

    [Fact]
    public async Task ManualRowsAfterTheImportDoNotPushTheCutoffForward()
    {
        var connectionId = await SeedMainBankConnectionAsync();

        await using (var seed = _db.CreateDbContext())
        {
            var account = seed.Accounts.First(a => a.SimpleFinAccountId == "bank-4");
            // A hand-entered correction dated well after the imported history.
            seed.Transactions.Add(new Transaction
            {
                AccountId = account.Id, Amount = -5m, Payee = "hand-entered",
                PostedDate = new DateOnly(2026, 8, 1), ImportBatchId = null,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = _db.CreateDbContext();
        var card = Single(await SyncCutoffCalculator.ForConnectionAsync(db, connectionId), "bank-4");

        // Still March. Letting the manual row win would hide four months of real transactions.
        Assert.Equal(new DateOnly(2026, 3, 23), card.LastImportedDate);
    }

    [Fact]
    public async Task AnAccountWithNoImportedHistoryHasNoCutoffAndKeepsEverything()
    {
        await using var db = _db.CreateDbContext();

        var connection = new SimpleFinConnection
        {
            InstitutionLabel = "Brokerage", EncryptedAccessUrl = "cipher",
        };
        db.SimpleFinConnections.Add(connection);
        await db.SaveChangesAsync();

        // AccountSetupService stamps snapshot accounts with today's date. If that were used as a
        // fallback cutoff, the first sync would discard every transaction it returned.
        db.Accounts.Add(new Account
        {
            Name = "Brokerage IRA",
            AccountType = AccountType.Investment,
            SimpleFinConnectionId = connection.Id,
            SimpleFinAccountId = "fid-1",
            OpeningBalanceDate = DateOnly.FromDateTime(DateTime.Today),
        });
        await db.SaveChangesAsync();

        var cutoffs = await SyncCutoffCalculator.ForConnectionAsync(db, connection.Id);

        Assert.Null(cutoffs[0].LastImportedDate);
        Assert.False(cutoffs[0].Discards(DateOnly.FromDateTime(DateTime.Today)));
        Assert.False(cutoffs[0].Discards(new DateOnly(2020, 1, 1)));
        // The normal window, not the first-sync one: this account is balance-only, so widening
        // for its benefit would fetch transactions that get discarded on arrival.
        Assert.Equal(
            SyncCutoffCalculator.EarliestReachableDate(Today),
            SyncCutoffCalculator.EarliestRequestDate(cutoffs, Today));
    }

    [Fact]
    public async Task AnAccountThatHasSyncedBeforeNoLongerAsksForEverything()
    {
        // A newly opened bank account has no import batch, so keying the "take everything" rule
        // off the import boundary would make this connection request a full history on every sync
        // for the rest of time. Once a row exists, the normal lookback window applies.
        await using var db = _db.CreateDbContext();

        var connection = new SimpleFinConnection { InstitutionLabel = "Main Bank", EncryptedAccessUrl = "cipher" };
        db.SimpleFinConnections.Add(connection);
        await db.SaveChangesAsync();

        var account = new Account
        {
            Name = "Main Bank Newly Opened", AccountType = AccountType.Savings,
            SimpleFinConnectionId = connection.Id, SimpleFinAccountId = "bank-new",
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        // First sync: nothing local, so reach back as far as the protocol allows.
        var before = await SyncCutoffCalculator.ForConnectionAsync(db, connection.Id);
        Assert.Equal(
            SyncCutoffCalculator.FirstSyncDate(Today),
            SyncCutoffCalculator.EarliestRequestDate(before, Today));

        db.Transactions.Add(new Transaction
        {
            AccountId = account.Id, Amount = 500m, Payee = "dividend",
            PostedDate = Today.AddDays(-3),
            Source = TransactionSource.SimpleFin, ExternalId = "TXN-1",
        });
        await db.SaveChangesAsync();

        var after = await SyncCutoffCalculator.ForConnectionAsync(db, connection.Id);
        Assert.Equal(
            SyncCutoffCalculator.EarliestReachableDate(Today),
            SyncCutoffCalculator.EarliestRequestDate(after, Today));
    }

    [Fact]
    public async Task OneUnsyncedAccountWidensTheWholeConnectionsWindow()
    {
        var connectionId = await SeedMainBankConnectionAsync();

        await using (var seed = _db.CreateDbContext())
        {
            seed.Accounts.Add(new Account
            {
                Name = "Main Bank Newly Opened",
                AccountType = AccountType.Savings,
                SimpleFinConnectionId = connectionId,
                SimpleFinAccountId = "bank-5",
            });
            await seed.SaveChangesAsync();
        }

        await using var db = _db.CreateDbContext();
        var cutoffs = await SyncCutoffCalculator.ForConnectionAsync(db, connectionId);

        // Widening costs payload; narrowing loses data. The imported accounts are still protected
        // by their own per-account cutoffs.
        Assert.Equal(
            SyncCutoffCalculator.FirstSyncDate(Today),
            SyncCutoffCalculator.EarliestRequestDate(cutoffs, Today));
        Assert.True(Single(cutoffs, "bank-1").Discards(new DateOnly(2026, 8, 7)));
    }

    [Fact]
    public void AConnectionWithNoAccountsStillSendsAStartDate()
    {
        // Never omit start-date: the Bridge's default returns almost nothing, so an omitted one
        // silently starves every account on the connection.
        Assert.Equal(
            SyncCutoffCalculator.EarliestReachableDate(Today),
            SyncCutoffCalculator.EarliestRequestDate([], Today));
    }

    // The server measures the window against the current UTC instant. Counted from the local
    // date instead, an evening sync west of Greenwich asks for more than the limit.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(23)]
    public void TheRequestStaysInsideTheLookbackAtAnyHour(int utcHour)
    {
        var now = new DateTimeOffset(2026, 8, 14, utcHour, 59, 59, TimeSpan.Zero);

        var start = SyncCutoffCalculator.EarliestRequestDate([], SyncCutoffCalculator.ServerToday(now));
        var span = now.ToUnixTimeSeconds() - SimpleFinClient.ToUnixSeconds(start);

        Assert.True(span < SyncCutoffCalculator.MaxLookbackDays * 86400L);
        Assert.True(span >= (SyncCutoffCalculator.MaxLookbackDays - 1) * 86400L);
    }

    [Fact]
    public void ServerTodayIsTheUtcDateNotTheLocalOne()
    {
        // Evening in a zone four hours behind UTC: already tomorrow as far as the server cares.
        var evening = new DateTimeOffset(2026, 8, 14, 20, 30, 0, TimeSpan.FromHours(-4));

        Assert.Equal(new DateOnly(2026, 8, 15), SyncCutoffCalculator.ServerToday(evening));
    }

    private static AccountCutoff Single(IEnumerable<AccountCutoff> cutoffs, string remoteId) =>
        cutoffs.Single(c => c.SimpleFinAccountId == remoteId);
}
