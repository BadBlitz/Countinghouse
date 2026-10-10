using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Entities;
using Countinghouse.Domain.Services;

namespace Countinghouse.Tests;

public class QifImportServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AccountService _accounts;
    private readonly TransactionService _transactions;
    private readonly CategoryService _categories;
    private readonly QifImportService _import;

    public QifImportServiceTests()
    {
        _accounts = new AccountService(_db);
        _transactions = new TransactionService(_db);
        _categories = new CategoryService(_db);
        _import = new QifImportService(_db);
    }

    public void Dispose() => _db.Dispose();

    // Group A accounts open at 0.00 so that OpeningBalance + sum(imported) lands on the real
    // balance — Quicken exports its own opening-balance row as the first transaction.
    private async Task<Account> CreateCheckingAsync(decimal opening = 0m) =>
        await _accounts.CreateAccountAsync(new Account
        {
            Name = "Sample Checking",
            AccountType = AccountType.Checking,
            OpeningBalance = opening,
            OpeningBalanceDate = new DateOnly(2013, 1, 1),
        });

    private static string Bank() => QifFixtures.Read("sample-bank.qif");

    [Fact]
    public async Task Import_InsertsEveryRowAndDerivesTheBalance()
    {
        var account = await CreateCheckingAsync();

        var (result, error) = await _import.ImportAsync(account.Id, Bank());

        Assert.Null(error);
        Assert.Equal(8, result!.Inserted);
        Assert.Equal(1712.87m, result.NewBalance);
        // BalanceCalculator must have run: the account row itself, not just the register sum.
        Assert.Equal(1712.87m, (await _accounts.GetAccountAsync(account.Id))!.CurrentBalance);
        Assert.Equal(1712.87m, (await _transactions.GetRegisterAsync(account.Id))[^1].RunningBalance);
    }

    [Fact]
    public async Task Import_MarksRowsAsManualWithNoExternalIdAndABatchId()
    {
        var account = await CreateCheckingAsync();

        var (result, _) = await _import.ImportAsync(account.Id, Bank());

        await using var db = _db.CreateDbContext();
        var rows = await db.Transactions.Where(t => t.AccountId == account.Id).ToListAsync();
        // Imported rows are deliberately indistinguishable from hand-entered ones to SimpleFIN,
        // which is why the batch id exists.
        Assert.All(rows, r => Assert.Equal(TransactionSource.Manual, r.Source));
        Assert.All(rows, r => Assert.Null(r.ExternalId));
        Assert.All(rows, r => Assert.Equal(result!.BatchId, r.ImportBatchId));
        Assert.All(rows, r => Assert.False(r.IsUserModified));
    }

    [Fact]
    public async Task Import_CarriesDateAmountPayeeMemoCheckNumberAndClearedStatus()
    {
        var account = await CreateCheckingAsync();
        await _import.ImportAsync(account.Id, Bank());

        var rows = (await _transactions.GetRegisterAsync(account.Id)).Select(r => r.Transaction).ToList();
        var fuel = rows.Single(t => t.Payee == "Invented Fuel Stop");

        Assert.Equal(new DateOnly(2013, 1, 15), fuel.PostedDate);
        Assert.Equal(-42.50m, fuel.Amount);
        Assert.Equal("Regular unleaded", fuel.Memo);
        Assert.Equal(ClearedStatus.Reconciled, fuel.ClearedStatus);
        Assert.Equal("1042", rows.Single(t => t.Payee == "Invented Check Deposit").CheckNumber);
    }

    [Fact]
    public async Task Import_LandsAlreadyReconciledHistoryAsReconciled()
    {
        var account = await CreateCheckingAsync();
        await _import.ImportAsync(account.Id, Bank());

        var rows = (await _transactions.GetRegisterAsync(account.Id)).Select(r => r.Transaction).ToList();

        // Six CX/CR rows, one C*, one blank C. Getting this wrong would pollute the first
        // reconciliation session with a decade of already-reconciled history.
        Assert.Equal(6, rows.Count(t => t.ClearedStatus == ClearedStatus.Reconciled));
        Assert.Equal(1, rows.Count(t => t.ClearedStatus == ClearedStatus.Cleared));
        Assert.Equal(1, rows.Count(t => t.ClearedStatus == ClearedStatus.Uncleared));
    }

    [Fact]
    public async Task Import_CreatesEveryCategoryFromTheCatBlockByDefault()
    {
        var account = await CreateCheckingAsync();

        await _import.ImportAsync(account.Id, Bank());

        var categories = await _categories.GetCategoriesAsync();
        // 7 from the !Type:Cat block (5 top-level + Auto:Fuel + Auto:Repairs) plus the two
        // bracketed transfer targets referenced by transactions.
        Assert.Equal(9, categories.Count);
        Assert.Contains(categories, c => c.FullName == "Auto:Repairs");
        // Present in the Cat block but referenced by no transaction — the reason eager creation
        // is the default, since the category list is global across all accounts.
        Assert.Contains(categories, c => c.Name == "Unused Income Cat");
    }

    [Fact]
    public async Task Import_WithoutEagerCategories_CreatesOnlyWhatTransactionsReference()
    {
        var account = await CreateCheckingAsync();

        await _import.ImportAsync(account.Id, Bank(), new QifImportOptions(CreateAllCategoriesFromFile: false));

        var categories = await _categories.GetCategoriesAsync();
        Assert.DoesNotContain(categories, c => c.Name == "Unused Income Cat");
        Assert.DoesNotContain(categories, c => c.Name == "Bank Charge");
        Assert.Contains(categories, c => c.FullName == "Auto:Fuel");
        // Types still come from the Cat block even when creation is lazy — the L field alone
        // can't tell Income from Expense.
        Assert.Equal(CategoryType.Income, categories.Single(c => c.Name == "Salary").CategoryType);
    }

    [Fact]
    public async Task Import_NestsSubcategoriesOneLevelUnderTheirParent()
    {
        var account = await CreateCheckingAsync();
        await _import.ImportAsync(account.Id, Bank());

        var rows = (await _transactions.GetRegisterAsync(account.Id)).Select(r => r.Transaction).ToList();
        Assert.Equal("Auto:Fuel", rows.Single(t => t.Payee == "Invented Fuel Stop").Category!.FullName);
        // The /tag suffix must not leak into the category name.
        Assert.Equal("Auto", rows.Single(t => t.Payee == "Invented Contractor").Category!.Name);
    }

    [Fact]
    public async Task Import_MakesClosedAccountTransferReferencesTransferCategories()
    {
        var account = await CreateCheckingAsync();
        await _import.ImportAsync(account.Id, Bank());

        var categories = await _categories.GetCategoriesAsync();
        var transfer = categories.Single(c => c.Name == "Sample Savings");
        // Transfer type is what keeps internal transfers out of spending reports without
        // auto-linking the two sides, which v1 doesn't do.
        Assert.Equal(CategoryType.Transfer, transfer.CategoryType);
    }

    [Fact]
    public async Task Import_GivesASplitParentTheCategoryOfItsLargestSplitLine()
    {
        var account = await CreateCheckingAsync();
        await _import.ImportAsync(account.Id, Bank());

        var rows = (await _transactions.GetRegisterAsync(account.Id)).Select(r => r.Transaction).ToList();
        var paycheck = rows.Single(t => t.Payee == "Invented Employer");

        Assert.Equal(1150.00m, paycheck.Amount);
        Assert.Equal("Salary", paycheck.Category!.Name);
        // No orphan rows: the split detail lines produce no transactions of their own.
        Assert.Equal(8, rows.Count);
        Assert.DoesNotContain(rows, t => t.Amount == 3500.00m);
    }

    [Fact]
    public async Task Import_LeavesRowsWithNoCategoryFieldUncategorized()
    {
        var account = await CreateCheckingAsync();
        await _import.ImportAsync(account.Id, Bank());

        var rows = (await _transactions.GetRegisterAsync(account.Id)).Select(r => r.Transaction).ToList();
        Assert.Null(rows.Single(t => t.Payee == "Invented Fee").CategoryId);
    }

    [Fact]
    public async Task Import_ReusesExistingCategoriesRatherThanDuplicatingThem()
    {
        var account = await CreateCheckingAsync();
        await _categories.CreateAsync("Salary", CategoryType.Income, null);

        await _import.ImportAsync(account.Id, Bank());

        var categories = await _categories.GetCategoriesAsync();
        Assert.Single(categories, c => c.Name == "Salary");
    }

    [Fact]
    public async Task Import_TwiceDuplicatesEveryRow()
    {
        // Documents the known hazard: QIF rows carry no stable id, so there is nothing to
        // dedupe against. The UI warns, and ClearImportedAsync is the way back.
        var account = await CreateCheckingAsync();

        await _import.ImportAsync(account.Id, Bank());
        await _import.ImportAsync(account.Id, Bank());

        Assert.Equal(16, (await _transactions.GetRegisterAsync(account.Id)).Count);
        Assert.Equal(3425.74m, (await _accounts.GetAccountAsync(account.Id))!.CurrentBalance);
    }

    [Fact]
    public async Task Import_RefusesAnInvestmentExport()
    {
        var account = await CreateCheckingAsync();

        var (result, error) = await _import.ImportAsync(account.Id, QifFixtures.Read("sample-invst.qif"));

        Assert.Null(result);
        Assert.Contains("Invst", error);
        Assert.Empty(await _transactions.GetRegisterAsync(account.Id));
    }

    [Fact]
    public async Task Import_RefusesAFileWithNoTransactionSection()
    {
        var account = await CreateCheckingAsync();

        var (result, error) = await _import.ImportAsync(account.Id, "!Type:Tag\r\nNSome tag\r\n^\r\n");

        Assert.Null(result);
        Assert.Contains("!Type:Bank", error);
    }

    [Fact]
    public async Task Import_RefusesAnUnknownAccount()
    {
        var (result, error) = await _import.ImportAsync(999, Bank());
        Assert.Null(result);
        Assert.Equal("Account not found.", error);
    }

    [Fact]
    public async Task Preview_ReportsCountsWithoutWritingAnything()
    {
        var account = await CreateCheckingAsync();

        var (preview, error) = await _import.PreviewAsync(account.Id, Bank());

        Assert.Null(error);
        Assert.Equal(8, preview!.TransactionCount);
        Assert.Equal(new DateOnly(2013, 1, 2), preview.FirstDate);
        Assert.Equal(new DateOnly(2020, 12, 31), preview.LastDate);
        Assert.Equal(1712.87m, preview.AmountTotal);
        Assert.Equal(1712.87m, preview.ProjectedBalance);
        Assert.Equal(1, preview.UncategorizedCount);
        Assert.Equal(2, preview.TransferCount);
        Assert.Equal(1, preview.SplitTransactionCount);
        Assert.Equal(1, preview.DominantSplitCategoryCount);
        Assert.Equal(7, preview.CategoriesInFile);
        Assert.Equal(0, preview.ExistingTransactionCount);
        Assert.Empty(preview.Warnings);

        // Nothing committed.
        Assert.Empty(await _transactions.GetRegisterAsync(account.Id));
        Assert.Empty(await _categories.GetCategoriesAsync());
    }

    [Fact]
    public async Task Preview_ProjectsOntoAnExistingOpeningBalanceAndPriorRows()
    {
        var account = await CreateCheckingAsync(opening: 100m);
        await _transactions.AddTransactionAsync(new Transaction
        {
            AccountId = account.Id,
            Amount = -25m,
            PostedDate = new DateOnly(2026, 1, 1),
            Payee = "Hand entered",
            Source = TransactionSource.Manual,
        });

        var (preview, _) = await _import.PreviewAsync(account.Id, Bank());

        Assert.Equal(1787.87m, preview!.ProjectedBalance);
        Assert.Equal(1, preview.ExistingTransactionCount);
        Assert.Contains(preview.Warnings, w => w.Contains("duplicates every row"));
    }

    [Fact]
    public async Task Preview_WarnsWhenTheAccountIsAlreadySyncedBySimpleFin()
    {
        var account = await CreateCheckingAsync();
        await using (var db = _db.CreateDbContext())
        {
            var connection = new SimpleFinConnection { InstitutionLabel = "Test" };
            db.SimpleFinConnections.Add(connection);
            await db.SaveChangesAsync();
            var tracked = await db.Accounts.FirstAsync(a => a.Id == account.Id);
            tracked.SimpleFinConnectionId = connection.Id;
            tracked.SimpleFinAccountId = "remote-1";
            await db.SaveChangesAsync();
        }

        var (preview, _) = await _import.PreviewAsync(account.Id, Bank());

        // The QIF/sync date-range overlap is the one thing nothing in the app prevents.
        Assert.Contains(preview!.Warnings, w => w.Contains("SimpleFIN"));
    }

    [Fact]
    public async Task Preview_SamplesTheFirstAndLastRowsInDateOrder()
    {
        var account = await CreateCheckingAsync();

        var (preview, _) = await _import.PreviewAsync(account.Id, Bank());

        Assert.Equal("Opening Balance", preview!.FirstRows[0].Payee);
        Assert.Equal("Invented Check Deposit", preview.LastRows[^1].Payee);
    }

    [Fact]
    public async Task ClearImported_RemovesImportedRowsButKeepsHandEnteredOnes()
    {
        var account = await CreateCheckingAsync(opening: 100m);
        await _transactions.AddTransactionAsync(new Transaction
        {
            AccountId = account.Id,
            Amount = -25m,
            PostedDate = new DateOnly(2026, 1, 1),
            Payee = "Hand entered",
            Source = TransactionSource.Manual,
        });
        await _import.ImportAsync(account.Id, Bank());

        var deleted = await _import.ClearImportedAsync(account.Id);

        Assert.Equal(8, deleted);
        var remaining = await _transactions.GetRegisterAsync(account.Id);
        Assert.Equal("Hand entered", remaining.Single().Transaction.Payee);
        // Balance must be recalculated, not left at the post-import figure.
        Assert.Equal(75m, (await _accounts.GetAccountAsync(account.Id))!.CurrentBalance);
    }

    [Fact]
    public async Task ClearImported_LeavesOtherAccountsAlone()
    {
        var first = await CreateCheckingAsync();
        var second = await _accounts.CreateAccountAsync(new Account
        {
            Name = "Other Checking",
            AccountType = AccountType.Checking,
            OpeningBalanceDate = new DateOnly(2013, 1, 1),
        });
        await _import.ImportAsync(first.Id, Bank());
        await _import.ImportAsync(second.Id, Bank());

        await _import.ClearImportedAsync(first.Id);

        Assert.Empty(await _transactions.GetRegisterAsync(first.Id));
        Assert.Equal(8, (await _transactions.GetRegisterAsync(second.Id)).Count);
    }

    [Fact]
    public async Task GetImportSummary_SeparatesImportedRowsFromManualOnes()
    {
        var account = await CreateCheckingAsync();
        await _transactions.AddTransactionAsync(new Transaction
        {
            AccountId = account.Id,
            Amount = -25m,
            PostedDate = new DateOnly(2026, 1, 1),
            Payee = "Hand entered",
            Source = TransactionSource.Manual,
        });
        await _import.ImportAsync(account.Id, Bank());
        await _import.ImportAsync(account.Id, QifFixtures.Read("sample-ccard.qif"));

        var summary = await _import.GetImportSummaryAsync(account.Id);

        Assert.Equal(11, summary.ImportedRowCount);
        Assert.Equal(2, summary.BatchCount);
        Assert.Equal(1, summary.ManualRowCount);
    }
}
