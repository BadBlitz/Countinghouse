using Countinghouse.Domain.Entities;
using Countinghouse.Domain.Services;

namespace Countinghouse.Tests;

public class AccountSetupServiceTests : IDisposable
{
    // A small fixture plan standing in for the real (uncommitted) accounts.json: three
    // importing accounts with distinct dated opening balances, plus two snapshot accounts
    // (one Investment, one Mortgage) with no opening balance date. Five accounts keeps the
    // 3/2 split visible without the noise of a full real-world inventory.
    private static readonly AccountPlan TestPlan = new(
    [
        new PlannedAccount
        {
            Name = "Test Checking",
            AccountType = AccountType.Checking,
            OpeningBalanceDate = new DateOnly(2013, 1, 1),
            ImportsHistory = true,
        },
        new PlannedAccount
        {
            Name = "Test Credit Card",
            AccountType = AccountType.CreditCard,
            OpeningBalanceDate = new DateOnly(2015, 4, 21),
            ImportsHistory = true,
        },
        new PlannedAccount
        {
            Name = "Test Savings",
            AccountType = AccountType.Savings,
            OpeningBalanceDate = new DateOnly(2018, 8, 19),
            ImportsHistory = true,
        },
        new PlannedAccount
        {
            Name = "Test Brokerage",
            AccountType = AccountType.Investment,
            ImportsHistory = false,
        },
        new PlannedAccount
        {
            Name = "Test Mortgage",
            AccountType = AccountType.Mortgage,
            ImportsHistory = false,
        },
    ]);

    private readonly TestDb _db = new();
    private readonly AccountService _accounts;
    private readonly AccountSetupService _setup;
    private readonly QifImportService _import;

    public AccountSetupServiceTests()
    {
        _accounts = new AccountService(_db);
        _setup = new AccountSetupService(_db, TestPlan);
        _import = new QifImportService(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void PlanShapeIsReportedFromTheSuppliedAccountPlan()
    {
        Assert.Equal(5, _setup.Plan.Count);
        Assert.Equal(3, _setup.Plan.Count(p => p.ImportsHistory));
        Assert.Equal(2, _setup.Plan.Count(p => p.NeedsBalance));
        // Names must be unique, since existing accounts are matched by name.
        Assert.Equal(5, _setup.Plan.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ImportingAccountsCarryADatedOpeningBalanceAndSnapshotAccountsDoNot()
    {
        // A fixed date is what keeps the register's opening row in the right place; snapshot
        // accounts get "today" at creation time instead.
        Assert.All(
            _setup.Plan.Where(p => p.ImportsHistory),
            p => Assert.NotNull(p.OpeningBalanceDate));
        Assert.All(
            _setup.Plan.Where(p => p.NeedsBalance),
            p => Assert.Null(p.OpeningBalanceDate));
    }

    [Fact]
    public async Task GetStatus_ReportsEverythingMissingOnAnEmptyDatabase()
    {
        var statuses = await _setup.GetStatusAsync();

        Assert.Equal(5, statuses.Count);
        Assert.All(statuses, s => Assert.False(s.Exists));
    }

    [Fact]
    public async Task CreateMissing_CreatesAllFiveWithTheRightTypesAndDates()
    {
        var result = await _setup.CreateMissingAsync();

        Assert.Equal(5, result.Created);
        var accounts = await _accounts.GetAccountsAsync();
        Assert.Equal(5, accounts.Count);

        var checking = accounts.Single(a => a.Name == "Test Checking");
        Assert.Equal(AccountType.Checking, checking.AccountType);
        // The plan's opening date, not "today" — that's what keeps the register's opening row
        // in the right place once history is imported.
        Assert.Equal(new DateOnly(2013, 1, 1), checking.OpeningBalanceDate);
        Assert.Equal(AccountType.Mortgage, accounts.Single(a => a.Name.Contains("Mortgage")).AccountType);
        Assert.Equal(1, accounts.Count(a => a.AccountType == AccountType.Investment));
    }

    [Fact]
    public async Task CreateMissing_OpensImportingAccountsAtZero()
    {
        // The documented trap: anything other than 0.00 here silently inflates the balance by
        // today's figure once history is imported.
        await _setup.CreateMissingAsync();

        var accounts = await _accounts.GetAccountsAsync();
        foreach (var planned in _setup.Plan.Where(p => p.ImportsHistory))
        {
            var account = accounts.Single(a => a.Name == planned.Name);
            Assert.Equal(0m, account.OpeningBalance);
            Assert.Equal(0m, account.CurrentBalance);
        }
    }

    [Fact]
    public async Task CreateMissing_AppliesSuppliedSnapshotBalancesToBothOpeningAndCurrent()
    {
        var balances = new Dictionary<string, decimal>
        {
            ["Test Brokerage"] = 41_250.19m,
            ["Test Mortgage"] = -183_400.00m,
        };

        await _setup.CreateMissingAsync(balances);

        var accounts = await _accounts.GetAccountsAsync();
        var brokerage = accounts.Single(a => a.Name == "Test Brokerage");
        Assert.Equal(41_250.19m, brokerage.OpeningBalance);
        // With no transactions, CurrentBalance is the opening balance — the dashboard reads
        // CurrentBalance, so leaving it at 0 would show the account as empty.
        Assert.Equal(41_250.19m, brokerage.CurrentBalance);
        Assert.Equal(-183_400.00m, accounts.Single(a => a.Name.Contains("Mortgage")).CurrentBalance);
        Assert.Equal(41_250.19m - 183_400.00m, await _accounts.GetNetWorthAsync());
    }

    [Fact]
    public async Task CreateMissing_LeavesSnapshotAccountsAtZeroWhenNoBalanceIsSupplied()
    {
        await _setup.CreateMissingAsync(new Dictionary<string, decimal>());

        var accounts = await _accounts.GetAccountsAsync();
        Assert.Equal(0m, accounts.Single(a => a.Name == "Test Brokerage").OpeningBalance);
    }

    [Fact]
    public async Task CreateMissing_IsIdempotent()
    {
        await _setup.CreateMissingAsync();

        var second = await _setup.CreateMissingAsync();

        Assert.Equal(0, second.Created);
        Assert.Equal(5, second.Skipped);
        Assert.Equal(5, (await _accounts.GetAccountsAsync()).Count);
    }

    [Fact]
    public async Task CreateMissing_NeverTouchesAnAccountThatAlreadyExists()
    {
        // Matching is by name, so a hand-created account must survive untouched — including a
        // balance the user already set.
        await _accounts.CreateAccountAsync(new Account
        {
            Name = "Test Checking",
            AccountType = AccountType.Savings,
            OpeningBalance = 999m,
            OpeningBalanceDate = new DateOnly(2020, 5, 5),
        });

        var result = await _setup.CreateMissingAsync();

        Assert.Equal(4, result.Created);
        var existing = (await _accounts.GetAccountsAsync()).Single(a => a.Name == "Test Checking");
        Assert.Equal(AccountType.Savings, existing.AccountType);
        Assert.Equal(999m, existing.OpeningBalance);
        Assert.Equal(new DateOnly(2020, 5, 5), existing.OpeningBalanceDate);
    }

    [Fact]
    public async Task GetStatus_LinksExistingAccountsToTheirIds()
    {
        await _setup.CreateMissingAsync();

        var statuses = await _setup.GetStatusAsync();

        Assert.All(statuses, s => Assert.True(s.Exists));
        Assert.All(statuses, s => Assert.NotNull(s.ExistingAccountId));
    }

    [Fact]
    public async Task GetStatus_MatchesExistingAccountsCaseInsensitively()
    {
        await _accounts.CreateAccountAsync(new Account
        {
            Name = "test checking",
            AccountType = AccountType.Checking,
            OpeningBalanceDate = new DateOnly(2013, 1, 1),
        });

        var statuses = await _setup.GetStatusAsync();

        Assert.True(statuses.Single(s => s.Planned.Name == "Test Checking").Exists);
        Assert.Equal(4, statuses.Count(s => !s.Exists));
    }

    [Fact]
    public async Task SeededImportingAccount_LandsOnTheImportedTotalWithNoInflation()
    {
        // The end-to-end point of opening at 0.00: seed, import, and the balance is exactly the
        // sum of the file's transactions — no double-count of a pre-typed balance.
        await _setup.CreateMissingAsync();
        var checking = (await _accounts.GetAccountsAsync()).Single(a => a.Name == "Test Checking");

        var (result, error) = await _import.ImportAsync(checking.Id, QifFixtures.Read("sample-bank.qif"));

        Assert.Null(error);
        Assert.Equal(1712.87m, result!.NewBalance);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyPlan()
    {
        // The plan file is an optional convenience — /settings/setup-accounts must degrade
        // gracefully rather than throw when nobody's created one yet.
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");

        var plan = AccountPlan.Load(path);

        Assert.Empty(plan.Accounts);
    }

    [Fact]
    public void Load_ParsesAccountTypeAndDatesAndOptionalNotes()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                [
                  {
                    "name": "Fixture Checking",
                    "accountType": "Checking",
                    "openingBalanceDate": "2013-01-01",
                    "importsHistory": true,
                    "notes": "from a QIF export"
                  },
                  {
                    "name": "Fixture Brokerage",
                    "accountType": "Investment",
                    "importsHistory": false
                  }
                ]
                """);

            var plan = AccountPlan.Load(path);

            var checking = plan.Accounts.Single(a => a.Name == "Fixture Checking");
            Assert.Equal(AccountType.Checking, checking.AccountType);
            Assert.Equal(new DateOnly(2013, 1, 1), checking.OpeningBalanceDate);
            Assert.Equal("from a QIF export", checking.Notes);
            Assert.False(checking.NeedsBalance);

            var brokerage = plan.Accounts.Single(a => a.Name == "Fixture Brokerage");
            Assert.Equal(AccountType.Investment, brokerage.AccountType);
            Assert.Null(brokerage.OpeningBalanceDate);
            // Notes is optional now, not required — a plan entry with none must still parse.
            Assert.Equal("", brokerage.Notes);
            Assert.True(brokerage.NeedsBalance);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ToleratesCommentsAndTrailingCommas()
    {
        // Docs/accounts.example.json is the documented format, comments and all — if the loader
        // can't parse its own example, the documentation is lying.
        var path = FindRepoRoot();
        path = Path.Combine(path, "Docs", "accounts.example.json");

        var plan = AccountPlan.Load(path);

        // Deliberately not asserting an exact count — the point is that the example parses at all,
        // and pinning the number would break the test every time an example account is added.
        Assert.NotEmpty(plan.Accounts);
        Assert.Contains(plan.Accounts, a => a.ImportsHistory && a.OpeningBalanceDate is not null);
        Assert.Contains(plan.Accounts, a => a.NeedsBalance && a.OpeningBalanceDate is null);
    }

    [Fact]
    public void Load_EmptyJsonArray_ReturnsEmptyPlan()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "[]");

            var plan = AccountPlan.Load(path);

            Assert.Empty(plan.Accounts);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Walks up from the test assembly's output directory to find the repo root, identified by
    // the solution file. Avoids hardcoding a bin/Debug/net10.0-shaped relative path that would
    // break on configuration or TFM changes.
    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Countinghouse.sln")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (Countinghouse.sln) above " + AppContext.BaseDirectory);
    }
}
