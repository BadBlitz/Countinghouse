using Countinghouse.Domain.Entities;
using Countinghouse.Domain.Services;

namespace Countinghouse.Tests;

public class ReportServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AccountService _accounts;
    private readonly TransactionService _transactions;
    private readonly CategoryService _categories;
    private readonly ReportService _reports;

    public ReportServiceTests()
    {
        _accounts = new AccountService(_db);
        _transactions = new TransactionService(_db);
        _categories = new CategoryService(_db);
        _reports = new ReportService(_db);
    }

    public void Dispose() => _db.Dispose();

    private Task<Account> CreateAccountAsync(string name = "Checking") =>
        _accounts.CreateAccountAsync(new Account
        {
            Name = name,
            AccountType = AccountType.Checking,
            OpeningBalance = 0m,
        });

    private async Task<Category> CreateCategoryAsync(string name, CategoryType type = CategoryType.Expense)
    {
        var (category, error) = await _categories.CreateAsync(name, type, null);
        Assert.Null(error);
        return category!;
    }

    private Task AddAsync(int accountId, decimal amount, DateOnly date, int? categoryId = null) =>
        _transactions.AddTransactionAsync(new Transaction
        {
            AccountId = accountId,
            Amount = amount,
            PostedDate = date,
            Payee = "Test",
            CategoryId = categoryId,
            Source = TransactionSource.Manual,
        });

    private static readonly DateOnly Jan = new(2026, 1, 15);
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2026, 1, 31);

    [Fact]
    public async Task GroupsByCategoryAndSumsAmounts()
    {
        var account = await CreateAccountAsync();
        var groceries = await CreateCategoryAsync("Groceries");
        var fuel = await CreateCategoryAsync("Fuel");

        await AddAsync(account.Id, -50m, Jan, groceries.Id);
        await AddAsync(account.Id, -30m, Jan, groceries.Id);
        await AddAsync(account.Id, -20m, Jan, fuel.Id);

        var report = await _reports.GetSpendingByCategoryAsync(From, To);

        Assert.Equal(3, report.TransactionCount);
        Assert.Equal(100m, report.TotalSpent);

        // Biggest outflow first, so the lines that matter are at the top.
        Assert.Equal(["Groceries", "Fuel"], report.Spending.Select(r => r.CategoryName));
        Assert.Equal(80m, report.Spending[0].Outflow);
        Assert.Equal(2, report.Spending[0].TransactionCount);
    }

    // A credit card payment appears as an outflow from checking and an inflow to the card. Counting
    // transfers would double it and make spending look far worse than it is.
    [Fact]
    public async Task ExcludesTransferCategories()
    {
        var account = await CreateAccountAsync();
        var groceries = await CreateCategoryAsync("Groceries");
        var transfer = await CreateCategoryAsync("Card Payment", CategoryType.Transfer);

        await AddAsync(account.Id, -50m, Jan, groceries.Id);
        await AddAsync(account.Id, -900m, Jan, transfer.Id);

        var report = await _reports.GetSpendingByCategoryAsync(From, To);

        Assert.Equal(1, report.TransactionCount);
        Assert.Equal(50m, report.TotalSpent);
        Assert.DoesNotContain(report.Spending, r => r.CategoryName == "Card Payment");
    }

    // The QIF history has plenty of uncategorized rows. Dropping them would understate every total
    // on the page while still looking like a complete report.
    [Fact]
    public async Task ReportsUncategorizedRowsUnderTheirOwnName()
    {
        var account = await CreateAccountAsync();
        await AddAsync(account.Id, -75m, Jan);

        var report = await _reports.GetSpendingByCategoryAsync(From, To);

        var row = Assert.Single(report.Spending);
        Assert.Equal(ReportService.UncategorizedName, row.CategoryName);
        Assert.Equal(75m, row.Outflow);
    }

    [Fact]
    public async Task SeparatesIncomeFromSpending()
    {
        var account = await CreateAccountAsync();
        var salary = await CreateCategoryAsync("Salary", CategoryType.Income);
        var groceries = await CreateCategoryAsync("Groceries");

        await AddAsync(account.Id, 2000m, Jan, salary.Id);
        await AddAsync(account.Id, -50m, Jan, groceries.Id);

        var report = await _reports.GetSpendingByCategoryAsync(From, To);

        Assert.Equal(2000m, report.TotalIncome);
        Assert.Equal(50m, report.TotalSpent);
        Assert.Equal(1950m, report.Net);
        Assert.Equal("Salary", Assert.Single(report.Income).CategoryName);
    }

    // A refund can outweigh the charges in its category. Net keeps the sign convention rather than
    // flipping the line into spending at a nonsense magnitude.
    [Fact]
    public async Task CategoryNettingPositive_LandsInIncomeNotSpending()
    {
        var account = await CreateAccountAsync();
        var streaming = await CreateCategoryAsync("Streaming");

        await AddAsync(account.Id, -10m, Jan, streaming.Id);
        await AddAsync(account.Id, 25m, Jan, streaming.Id);

        var report = await _reports.GetSpendingByCategoryAsync(From, To);

        Assert.Empty(report.Spending);
        Assert.Equal(15m, Assert.Single(report.Income).Total);
    }

    [Fact]
    public async Task RespectsDateRange()
    {
        var account = await CreateAccountAsync();
        var groceries = await CreateCategoryAsync("Groceries");

        await AddAsync(account.Id, -10m, new DateOnly(2025, 12, 31), groceries.Id);
        await AddAsync(account.Id, -20m, From, groceries.Id);
        await AddAsync(account.Id, -30m, To, groceries.Id);
        await AddAsync(account.Id, -40m, new DateOnly(2026, 2, 1), groceries.Id);

        var report = await _reports.GetSpendingByCategoryAsync(From, To);

        // Both bounds inclusive: a statement period includes its first and last day.
        Assert.Equal(2, report.TransactionCount);
        Assert.Equal(50m, report.TotalSpent);
    }

    [Fact]
    public async Task FiltersByAccount()
    {
        var checking = await CreateAccountAsync("Checking");
        var card = await CreateAccountAsync("Card");
        var groceries = await CreateCategoryAsync("Groceries");

        await AddAsync(checking.Id, -10m, Jan, groceries.Id);
        await AddAsync(card.Id, -90m, Jan, groceries.Id);

        var report = await _reports.GetSpendingByCategoryAsync(From, To, [card.Id]);

        Assert.Equal(1, report.TransactionCount);
        Assert.Equal(90m, report.TotalSpent);
    }

    [Fact]
    public async Task EmptyRange_ReportsEmptyRatherThanThrowing()
    {
        await CreateAccountAsync();

        var report = await _reports.GetSpendingByCategoryAsync(From, To);

        Assert.True(report.IsEmpty);
        Assert.Equal(0m, report.TotalSpent);
        Assert.Empty(report.Spending);
    }
}
