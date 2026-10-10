using System.Net;
using System.Globalization;
using Countinghouse.Domain.Entities;
using Countinghouse.SimpleFin;

namespace Countinghouse.Tests;

public class SimpleFinSyncServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private const string AccessUrl = "https://user:pass@beta-bridge.simplefin.org/simplefin";

    // A protector that does nothing but prefix, so the sync tests exercise real code paths
    // without needing a Data Protection key ring.
    private sealed class FakeProtector : IAccessUrlProtector
    {
        public bool DecryptionFails { get; init; }

        public string Protect(string accessUrl) => "enc:" + accessUrl;

        public bool TryUnprotect(string encryptedAccessUrl, out string accessUrl)
        {
            if (DecryptionFails)
            {
                accessUrl = "";
                return false;
            }
            accessUrl = encryptedAccessUrl.StartsWith("enc:") ? encryptedAccessUrl[4..] : encryptedAccessUrl;
            return true;
        }
    }

    // Midday UTC, matching how SimpleFIN normalizes its timestamps.
    private static long Epoch(int year, int month, int day) =>
        new DateTimeOffset(new DateTime(year, month, day, 12, 0, 0), TimeSpan.Zero).ToUnixTimeSeconds();

    private static string Response(string remoteAccountId, decimal balance, params string[] transactions) =>
        $$"""
        {
          "errors": [],
          "accounts": [{
            "org": { "name": "Example Bank", "sfin-url": "https://x/simplefin" },
            "id": "{{remoteAccountId}}",
            "name": "Everyday Checking",
            "currency": "USD",
            "balance": "{{balance.ToString(CultureInfo.InvariantCulture)}}",
            "balance-date": {{Epoch(2026, 8, 12)}},
            "transactions": [{{string.Join(",", transactions)}}]
          }]
        }
        """;

    private static string Txn(string id, int year, int month, int day, decimal amount, bool pending = false) =>
        $$"""
        { "id": "{{id}}", "posted": {{Epoch(year, month, day)}},
          "amount": "{{amount.ToString(CultureInfo.InvariantCulture)}}",
          "description": "SAMPLE {{id}}", "pending": {{(pending ? "true" : "false")}} }
        """;

    private SimpleFinSyncService ServiceFor(string json, FakeProtector? protector = null) =>
        new(_db,
            new SimpleFinClient(StubHttpMessageHandler.Client(HttpStatusCode.OK, json)),
            protector ?? new FakeProtector());

    // One connection, one account, optionally with imported QIF history ending on a given date.
    private async Task<(int ConnectionId, int AccountId)> SeedAsync(DateOnly? lastImported = null)
    {
        await using var db = _db.CreateDbContext();

        var connection = new SimpleFinConnection
        {
            InstitutionLabel = "Example Bank",
            EncryptedAccessUrl = "enc:" + AccessUrl,
        };
        db.SimpleFinConnections.Add(connection);
        await db.SaveChangesAsync();

        var account = new Account
        {
            Name = "Main Bank Checking",
            AccountType = AccountType.Checking,
            SimpleFinConnectionId = connection.Id,
            SimpleFinAccountId = "remote-1",
            OpeningBalance = 0m,
            OpeningBalanceDate = new DateOnly(2013, 1, 1),
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        if (lastImported is { } last)
        {
            db.Transactions.Add(new Transaction
            {
                AccountId = account.Id, Amount = -1m, Payee = "imported row",
                PostedDate = last, ImportBatchId = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }

        return (connection.Id, account.Id);
    }

    [Fact]
    public async Task NewTransactionsAreInsertedAsSimpleFinRowsWithTheirExternalId()
    {
        var (connectionId, accountId) = await SeedAsync();
        var service = ServiceFor(Response("remote-1", 500m,
            Txn("TXN-1", 2026, 8, 10, -25.50m),
            Txn("TXN-2", 2026, 8, 11, 100m)));

        var result = await service.SyncConnectionAsync(connectionId);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Accounts[0].Inserted);

        await using var db = _db.CreateDbContext();
        var rows = db.Transactions.Where(t => t.AccountId == accountId).OrderBy(t => t.PostedDate).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(TransactionSource.SimpleFin, r.Source));
        Assert.Equal("TXN-1", rows[0].ExternalId);
        Assert.Equal(-25.50m, rows[0].Amount);
        Assert.Equal(new DateOnly(2026, 8, 10), rows[0].PostedDate);
        // Imported rows must never carry a batch id, or ClearImportedAsync would delete them.
        Assert.All(rows, r => Assert.Null(r.ImportBatchId));
    }

    [Fact]
    public async Task ResyncingTheSameTransactionUpdatesRatherThanDuplicates()
    {
        var (connectionId, _) = await SeedAsync();

        await ServiceFor(Response("remote-1", 500m, Txn("TXN-1", 2026, 8, 10, -25.50m)))
            .SyncConnectionAsync(connectionId);

        // Same id, corrected amount — what a bank does when a pending charge settles.
        var result = await ServiceFor(Response("remote-1", 500m, Txn("TXN-1", 2026, 8, 10, -27.00m)))
            .SyncConnectionAsync(connectionId);

        Assert.Equal(0, result.Accounts[0].Inserted);
        Assert.Equal(1, result.Accounts[0].Updated);

        await using var db = _db.CreateDbContext();
        var row = Assert.Single(db.Transactions.Where(t => t.ExternalId == "TXN-1"));
        Assert.Equal(-27.00m, row.Amount);
    }

    [Fact]
    public async Task AHandEditedRowKeepsItsAmountAndDateOnResync()
    {
        var (connectionId, _) = await SeedAsync();
        await ServiceFor(Response("remote-1", 500m, Txn("TXN-1", 2026, 8, 10, -25.50m)))
            .SyncConnectionAsync(connectionId);

        await using (var edit = _db.CreateDbContext())
        {
            var row = edit.Transactions.First(t => t.ExternalId == "TXN-1");
            row.Amount = -99.99m;
            row.IsUserModified = true;
            await edit.SaveChangesAsync();
        }

        var result = await ServiceFor(Response("remote-1", 500m, Txn("TXN-1", 2026, 8, 10, -25.50m)))
            .SyncConnectionAsync(connectionId);

        Assert.Equal(1, result.Accounts[0].ProtectedByUserEdit);
        Assert.Equal(0, result.Accounts[0].Updated);

        await using var db = _db.CreateDbContext();
        var kept = db.Transactions.First(t => t.ExternalId == "TXN-1");
        Assert.Equal(-99.99m, kept.Amount);
    }

    [Fact]
    public async Task TransactionsAtOrBeforeTheImportCutoffAreSkipped()
    {
        // Imported history ends 2026-08-07, so the connection asks from 08-08 — but a sibling
        // account can widen the window and drag older rows in. They must not be inserted.
        var (connectionId, accountId) = await SeedAsync(lastImported: new DateOnly(2026, 8, 7));

        var result = await ServiceFor(Response("remote-1", 500m,
                Txn("OLD-1", 2026, 3, 15, -10m),
                Txn("OLD-2", 2026, 8, 7, -20m),   // boundary: on the cutoff, already imported
                Txn("NEW-1", 2026, 8, 8, -30m)))
            .SyncConnectionAsync(connectionId);

        Assert.Equal(2, result.Accounts[0].SkippedBeforeCutoff);
        Assert.Equal(1, result.Accounts[0].Inserted);

        await using var db = _db.CreateDbContext();
        var synced = db.Transactions.Where(t => t.AccountId == accountId && t.ExternalId != null).ToList();
        Assert.Equal("NEW-1", Assert.Single(synced).ExternalId);
    }

    [Fact]
    public async Task BalanceComesFromTheApiAndIsNotDerivedFromTheRegister()
    {
        var (connectionId, accountId) = await SeedAsync();

        // The register sums to -25.50; the API says the account holds 1284.37. The API wins,
        // because the lookback window isn't guaranteed to cover full history.
        await ServiceFor(Response("remote-1", 1284.37m, Txn("TXN-1", 2026, 8, 10, -25.50m)))
            .SyncConnectionAsync(connectionId);

        await using var db = _db.CreateDbContext();
        var account = db.Accounts.First(a => a.Id == accountId);
        Assert.Equal(1284.37m, account.CurrentBalance);
        // BalanceAsOf is rendered in local time, so assert the local view of that UTC instant.
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(Epoch(2026, 8, 12)).LocalDateTime,
            account.BalanceAsOf);
    }

    [Fact]
    public async Task PendingRowsAreUnclearedAndPostedRowsAreCleared()
    {
        var (connectionId, _) = await SeedAsync();

        await ServiceFor(Response("remote-1", 0m,
                Txn("SETTLED", 2026, 8, 10, -25m),
                Txn("PENDING", 2026, 8, 11, -30m, pending: true)))
            .SyncConnectionAsync(connectionId);

        await using var db = _db.CreateDbContext();
        Assert.Equal(ClearedStatus.Cleared, db.Transactions.First(t => t.ExternalId == "SETTLED").ClearedStatus);
        Assert.Equal(ClearedStatus.Uncleared, db.Transactions.First(t => t.ExternalId == "PENDING").ClearedStatus);
    }

    [Fact]
    public async Task AReconciledRowIsNeverWalkedBackToCleared()
    {
        var (connectionId, _) = await SeedAsync();
        await ServiceFor(Response("remote-1", 0m, Txn("TXN-1", 2026, 8, 10, -25m)))
            .SyncConnectionAsync(connectionId);

        await using (var reconcile = _db.CreateDbContext())
        {
            var row = reconcile.Transactions.First(t => t.ExternalId == "TXN-1");
            row.ClearedStatus = ClearedStatus.Reconciled;
            await reconcile.SaveChangesAsync();
        }

        await ServiceFor(Response("remote-1", 0m, Txn("TXN-1", 2026, 8, 10, -25m)))
            .SyncConnectionAsync(connectionId);

        await using var db = _db.CreateDbContext();
        // Downgrading this would silently reopen a closed statement.
        Assert.Equal(ClearedStatus.Reconciled, db.Transactions.First(t => t.ExternalId == "TXN-1").ClearedStatus);
    }

    [Fact]
    public async Task AnUnmappedRemoteAccountIsReportedRatherThanAutoCreated()
    {
        var (connectionId, _) = await SeedAsync();

        var result = await ServiceFor(Response("remote-UNKNOWN", 100m, Txn("TXN-1", 2026, 8, 10, -5m)))
            .SyncConnectionAsync(connectionId);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Accounts);
        Assert.Single(result.UnmappedRemoteAccounts);

        await using var db = _db.CreateDbContext();
        Assert.Empty(db.Transactions.Where(t => t.ExternalId != null));
        Assert.Single(db.Accounts);
    }

    [Fact]
    public async Task UndecryptableCredentialsAskForARelinkAndTouchNothing()
    {
        var (connectionId, _) = await SeedAsync();
        var service = ServiceFor(
            Response("remote-1", 100m, Txn("TXN-1", 2026, 8, 10, -5m)),
            new FakeProtector { DecryptionFails = true });

        var result = await service.SyncConnectionAsync(connectionId);

        Assert.False(result.Succeeded);
        Assert.True(result.RequiresRelink);

        await using var db = _db.CreateDbContext();
        Assert.Empty(db.Transactions.Where(t => t.ExternalId != null));
        // The failure is recorded, so a dead connection can't look like a quiet one.
        Assert.Equal("NeedsRelink", db.SimpleFinConnections.First().LastSyncStatus);
    }

    [Fact]
    public async Task PaymentRequiredIsRecordedWithoutAskingForARelink()
    {
        var (connectionId, _) = await SeedAsync();
        var service = new SimpleFinSyncService(
            _db,
            new SimpleFinClient(StubHttpMessageHandler.Client(HttpStatusCode.PaymentRequired, "")),
            new FakeProtector());

        var result = await service.SyncConnectionAsync(connectionId);

        Assert.False(result.Succeeded);
        Assert.False(result.RequiresRelink);

        await using var db = _db.CreateDbContext();
        Assert.Equal("Failed", db.SimpleFinConnections.First().LastSyncStatus);
    }

    [Fact]
    public async Task APartialFailureIsSurfacedAndRecordedRatherThanLookingLikeAQuietSync()
    {
        var (connectionId, _) = await SeedAsync();
        var json = $$"""
        {
          "errors": ["Connection to Example Bank failed."],
          "accounts": [{
            "org": { "name": "Example Bank", "sfin-url": "https://x/simplefin" },
            "id": "remote-1", "name": "Everyday Checking", "currency": "USD",
            "balance": "100.00", "balance-date": {{Epoch(2026, 8, 12)}},
            "transactions": []
          }]
        }
        """;

        var result = await ServiceFor(json).SyncConnectionAsync(connectionId);

        Assert.True(result.Succeeded);
        Assert.Single(result.RemoteErrors);

        await using var db = _db.CreateDbContext();
        Assert.Equal("PartialSuccess", db.SimpleFinConnections.First().LastSyncStatus);
    }

    // Two institutions behind one Access URL, plus a third mapped account the response omits.
    private async Task<int> SeedTwoInstitutionsAsync()
    {
        var (connectionId, _) = await SeedAsync();
        await using var db = _db.CreateDbContext();
        foreach (var (name, remoteId) in new[] { ("Card Issuer Card", "remote-2"), ("Savings #1", "remote-3") })
        {
            db.Accounts.Add(new Account
            {
                Name = name,
                AccountType = AccountType.Savings,
                SimpleFinConnectionId = connectionId,
                SimpleFinAccountId = remoteId,
            });
        }
        await db.SaveChangesAsync();
        return connectionId;
    }

    private static string TwoInstitutionResponse(params string[] errors) =>
        $$"""
        {
          "errors": [{{string.Join(",", errors.Select(e => $"\"{e}\""))}}],
          "accounts": [{
            "org": { "name": "Example Bank", "sfin-url": "https://x/simplefin" },
            "id": "remote-1", "name": "Everyday Checking", "currency": "USD",
            "balance": "100.00", "balance-date": {{Epoch(2026, 8, 12)}},
            "transactions": []
          }, {
            "org": { "name": "Card Issuer", "sfin-url": "https://x/simplefin" },
            "id": "remote-2", "name": "Card", "currency": "USD",
            "balance": "-50.00", "balance-date": {{Epoch(2026, 8, 9)}},
            "transactions": []
          }]
        }
        """;

    private async Task<Dictionary<string, string?>> SyncErrorsByRemoteIdAsync()
    {
        await using var db = _db.CreateDbContext();
        return db.Accounts.ToDictionary(a => a.SimpleFinAccountId!, a => a.LastSyncError);
    }

    [Fact]
    public async Task AnErrorNamingOneInstitutionIsPinnedOnItsAccountsOnly()
    {
        var connectionId = await SeedTwoInstitutionsAsync();
        var json = TwoInstitutionResponse("Connection to Card Issuer may need attention.");

        await ServiceFor(json).SyncConnectionAsync(connectionId);

        var errors = await SyncErrorsByRemoteIdAsync();
        Assert.Null(errors["remote-1"]);
        Assert.Contains("Card Issuer", errors["remote-2"]);
        // Left out of the response while something was failing, so it can't be cleared.
        Assert.Contains("Card Issuer", errors["remote-3"]);
    }

    [Fact]
    public async Task AnErrorNamingNoKnownInstitutionIsPinnedOnEveryAccount()
    {
        var connectionId = await SeedTwoInstitutionsAsync();

        await ServiceFor(TwoInstitutionResponse("Something went wrong.")).SyncConnectionAsync(connectionId);

        Assert.All((await SyncErrorsByRemoteIdAsync()).Values, error => Assert.NotNull(error));
    }

    [Fact]
    public async Task ACleanSyncClearsAnEarlierAccountError()
    {
        var connectionId = await SeedTwoInstitutionsAsync();
        await ServiceFor(TwoInstitutionResponse("Connection to Card Issuer may need attention."))
            .SyncConnectionAsync(connectionId);

        await ServiceFor(TwoInstitutionResponse()).SyncConnectionAsync(connectionId);

        Assert.All((await SyncErrorsByRemoteIdAsync()).Values, Assert.Null);
    }

    [Fact]
    public async Task AnInvestmentAccountTakesTheBalanceAndNoTransactions()
    {
        var (connectionId, accountId) = await SeedAsync();

        await using (var seed = _db.CreateDbContext())
        {
            seed.Accounts.First(a => a.Id == accountId).AccountType = AccountType.Investment;
            await seed.SaveChangesAsync();
        }

        var result = await ServiceFor(Response("remote-1", 40000.00m,
                Txn("TXN-1", 2026, 8, 10, -120.00m),
                Txn("TXN-2", 2026, 8, 11, 7.78m)))
            .SyncConnectionAsync(connectionId);

        var account = result.Accounts[0];
        Assert.True(account.BalanceOnly);
        Assert.Equal(0, account.Inserted);
        Assert.Equal(40000.00m, account.Balance);

        await using var db = _db.CreateDbContext();
        Assert.Empty(db.Transactions.Where(t => t.Source == TransactionSource.SimpleFin));
        Assert.Equal(40000.00m, db.Accounts.First(a => a.Id == accountId).CurrentBalance);
    }

    [Fact]
    public async Task SwitchingAnAccountToBalanceOnlyClearsWhatItAlreadySyncedButKeepsTheUsersRows()
    {
        // Imported history as well as a manual row, since both must survive.
        var (connectionId, accountId) = await SeedAsync(lastImported: new DateOnly(2026, 8, 7));

        await ServiceFor(Response("remote-1", 100m, Txn("TXN-1", 2026, 8, 10, -120.00m)))
            .SyncConnectionAsync(connectionId);

        await using (var seed = _db.CreateDbContext())
        {
            seed.Accounts.First(a => a.Id == accountId).AccountType = AccountType.Investment;
            seed.Transactions.Add(new Transaction
            {
                AccountId = accountId, Amount = -20m, Payee = "hand-entered",
                PostedDate = new DateOnly(2026, 8, 10), Source = TransactionSource.Manual,
            });
            await seed.SaveChangesAsync();
        }

        var result = await ServiceFor(Response("remote-1", 40000.00m, Txn("TXN-1", 2026, 8, 10, -120.00m)))
            .SyncConnectionAsync(connectionId);

        Assert.Equal(1, result.Accounts[0].RemovedStaleRows);

        await using var db = _db.CreateDbContext();
        Assert.Empty(db.Transactions.Where(t => t.Source == TransactionSource.SimpleFin));
        // The user's own rows are not the sync's to delete.
        Assert.Single(db.Transactions.Where(t => t.Payee == "hand-entered"));
        Assert.Single(db.Transactions.Where(t => t.ImportBatchId != null));
    }

    [Fact]
    public async Task AnInvestmentAccountCanOptBackIntoTransactions()
    {
        var (connectionId, accountId) = await SeedAsync();

        await using (var seed = _db.CreateDbContext())
        {
            var account = seed.Accounts.First(a => a.Id == accountId);
            account.AccountType = AccountType.Investment;
            account.SyncTransactionsOverride = true;
            await seed.SaveChangesAsync();
        }

        var result = await ServiceFor(Response("remote-1", 100m, Txn("TXN-1", 2026, 8, 10, -120.00m)))
            .SyncConnectionAsync(connectionId);

        Assert.False(result.Accounts[0].BalanceOnly);
        Assert.Equal(1, result.Accounts[0].Inserted);
    }

    [Fact]
    public async Task ManualRowsInTheSameAccountAreLeftAlone()
    {
        var (connectionId, accountId) = await SeedAsync();

        await using (var seed = _db.CreateDbContext())
        {
            seed.Transactions.Add(new Transaction
            {
                AccountId = accountId, Amount = -12m, Payee = "hand-entered",
                PostedDate = new DateOnly(2026, 8, 10), Source = TransactionSource.Manual,
            });
            await seed.SaveChangesAsync();
        }

        await ServiceFor(Response("remote-1", 0m, Txn("TXN-1", 2026, 8, 10, -25m)))
            .SyncConnectionAsync(connectionId);

        await using var db = _db.CreateDbContext();
        var manual = db.Transactions.First(t => t.Payee == "hand-entered");
        Assert.Equal(-12m, manual.Amount);
        Assert.Null(manual.ExternalId);
    }

    // Synced accounts take BalanceCalculator's early return, so the sync itself is the only thing
    // that can snapshot them. The date comes from the feed's balance-date, not from today.
    [Fact]
    public async Task SyncRecordsABalanceSnapshotDatedFromTheFeed()
    {
        var (connectionId, accountId) = await SeedAsync();

        await ServiceFor(Response("remote-1", 1234.56m, Txn("TXN-1", 2026, 8, 10, -25m)))
            .SyncConnectionAsync(connectionId);

        await using var db = _db.CreateDbContext();
        var snapshot = Assert.Single(db.AccountBalanceSnapshots.Where(s => s.AccountId == accountId));
        Assert.Equal(1234.56m, snapshot.Balance);
        Assert.Equal(new DateOnly(2026, 8, 12), snapshot.AsOfDate);
    }

    // These accounts hold no register at all, so the snapshot series is the only history they will
    // ever have — and they carry most of the net worth.
    [Fact]
    public async Task BalanceOnlyAccountIsSnapshottedToo()
    {
        var (connectionId, accountId) = await SeedAsync();

        await using (var seed = _db.CreateDbContext())
        {
            seed.Accounts.First(a => a.Id == accountId).AccountType = AccountType.Investment;
            await seed.SaveChangesAsync();
        }

        var result = await ServiceFor(Response("remote-1", 40100.00m)).SyncConnectionAsync(connectionId);
        Assert.True(result.Accounts[0].BalanceOnly);

        await using var db = _db.CreateDbContext();
        var snapshot = Assert.Single(db.AccountBalanceSnapshots.Where(s => s.AccountId == accountId));
        Assert.Equal(40100.00m, snapshot.Balance);
    }

    // Syncing repeatedly in a day is normal — the unique index on (AccountId, AsOfDate) means the
    // second run has to update the row rather than insert a second one.
    [Fact]
    public async Task SyncingTwiceInADayKeepsOneSnapshotAtTheLaterBalance()
    {
        var (connectionId, accountId) = await SeedAsync();

        await ServiceFor(Response("remote-1", 100m)).SyncConnectionAsync(connectionId);
        await ServiceFor(Response("remote-1", 175m)).SyncConnectionAsync(connectionId);

        await using var db = _db.CreateDbContext();
        var snapshot = Assert.Single(db.AccountBalanceSnapshots.Where(s => s.AccountId == accountId));
        Assert.Equal(175m, snapshot.Balance);
    }
    // --- category rules on sync ---

    private async Task<int> AddRuleAsync(string pattern, CategoryRuleMatch match)
    {
        await using var db = _db.CreateDbContext();
        var category = new Category { Name = "Groceries", CategoryType = CategoryType.Expense };
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        db.CategoryRules.Add(new CategoryRule
        {
            Pattern = pattern, MatchType = match, CategoryId = category.Id,
        });
        await db.SaveChangesAsync();
        return category.Id;
    }

    // SimpleFIN carries no category, so without rules every synced row lands uncategorized.
    [Fact]
    public async Task SyncAppliesCategoryRulesToNewRows()
    {
        var (connectionId, accountId) = await SeedAsync();
        var categoryId = await AddRuleAsync("SAMPLE", CategoryRuleMatch.Contains);
        var service = ServiceFor(Response("remote-1", 500m,
            Txn("TXN-1", 2026, 8, 10, -25.50m),
            Txn("TXN-2", 2026, 8, 11, 100m)));

        var result = await service.SyncConnectionAsync(connectionId);

        Assert.Equal(2, result.Accounts[0].AutoCategorized);
        await using var db = _db.CreateDbContext();
        var rows = db.Transactions.Where(t => t.AccountId == accountId).ToList();
        Assert.All(rows, r => Assert.Equal(categoryId, r.CategoryId));
        Assert.Equal(2, db.CategoryRules.First().TimesApplied);
    }

    [Fact]
    public async Task SyncLeavesRowsUncategorizedWhenNoRuleMatches()
    {
        var (connectionId, accountId) = await SeedAsync();
        await AddRuleAsync("Grocerly", CategoryRuleMatch.Exact);
        var service = ServiceFor(Response("remote-1", 500m, Txn("TXN-1", 2026, 8, 10, -25.50m)));

        var result = await service.SyncConnectionAsync(connectionId);

        Assert.Equal(0, result.Accounts[0].AutoCategorized);
        await using var db = _db.CreateDbContext();
        Assert.Null(db.Transactions.First(t => t.AccountId == accountId).CategoryId);
    }

    // A re-sync updates amount/payee on an existing row; it must not re-run rules over a category
    // the user has since corrected by hand.
    [Fact]
    public async Task SyncDoesNotRecategorizeAnExistingRow()
    {
        var (connectionId, accountId) = await SeedAsync();
        await AddRuleAsync("SAMPLE", CategoryRuleMatch.Contains);
        var json = Response("remote-1", 500m, Txn("TXN-1", 2026, 8, 10, -25.50m));
        await ServiceFor(json).SyncConnectionAsync(connectionId);

        int correctedId;
        await using (var db = _db.CreateDbContext())
        {
            var corrected = new Category { Name = "Dining", CategoryType = CategoryType.Expense };
            db.Categories.Add(corrected);
            await db.SaveChangesAsync();
            correctedId = corrected.Id;

            var row = db.Transactions.First(t => t.AccountId == accountId);
            row.CategoryId = correctedId;
            await db.SaveChangesAsync();
        }

        var result = await ServiceFor(json).SyncConnectionAsync(connectionId);

        Assert.Equal(0, result.Accounts[0].AutoCategorized);
        await using var check = _db.CreateDbContext();
        Assert.Equal(correctedId, check.Transactions.First(t => t.AccountId == accountId).CategoryId);
    }

    [Fact]
    public async Task SyncIgnoresDisabledRules()
    {
        var (connectionId, accountId) = await SeedAsync();
        await AddRuleAsync("SAMPLE", CategoryRuleMatch.Contains);
        await using (var db = _db.CreateDbContext())
        {
            var rule = db.CategoryRules.First();
            rule.IsEnabled = false;
            await db.SaveChangesAsync();
        }

        await ServiceFor(Response("remote-1", 500m, Txn("TXN-1", 2026, 8, 10, -25.50m)))
            .SyncConnectionAsync(connectionId);

        await using var check = _db.CreateDbContext();
        Assert.Null(check.Transactions.First(t => t.AccountId == accountId).CategoryId);
    }

}
