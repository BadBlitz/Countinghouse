using Countinghouse.Domain.Entities;
using Countinghouse.Domain.Services;

namespace Countinghouse.Tests;

public class TransactionServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AccountService _accounts;
    private readonly TransactionService _transactions;

    public TransactionServiceTests()
    {
        _accounts = new AccountService(_db);
        _transactions = new TransactionService(_db);
    }

    public void Dispose() => _db.Dispose();

    private async Task<Account> CreateCheckingAsync(decimal opening = 100m)
    {
        return await _accounts.CreateAccountAsync(new Account
        {
            Name = "Checking",
            AccountType = AccountType.Checking,
            OpeningBalance = opening,
            OpeningBalanceDate = new DateOnly(2026, 1, 1),
        });
    }

    private static Transaction NewTxn(int accountId, decimal amount, DateOnly date, string payee = "Test") =>
        new()
        {
            AccountId = accountId,
            Amount = amount,
            PostedDate = date,
            Payee = payee,
            Source = TransactionSource.Manual,
        };

    [Fact]
    public async Task AddTransaction_UpdatesManualAccountBalance()
    {
        var account = await CreateCheckingAsync(100m);
        await _transactions.AddTransactionAsync(NewTxn(account.Id, -25.50m, new DateOnly(2026, 1, 5)));
        await _transactions.AddTransactionAsync(NewTxn(account.Id, 10m, new DateOnly(2026, 1, 6)));

        var reloaded = await _accounts.GetAccountAsync(account.Id);
        Assert.Equal(84.50m, reloaded!.CurrentBalance);
    }

    [Fact]
    public async Task Register_ComputesRunningBalanceInDateOrder()
    {
        var account = await CreateCheckingAsync(100m);
        // Inserted out of order on purpose; register must sort by date.
        await _transactions.AddTransactionAsync(NewTxn(account.Id, -30m, new DateOnly(2026, 1, 10)));
        await _transactions.AddTransactionAsync(NewTxn(account.Id, 50m, new DateOnly(2026, 1, 2)));

        var register = await _transactions.GetRegisterAsync(account.Id);

        Assert.Equal(2, register.Count);
        Assert.Equal(new DateOnly(2026, 1, 2), register[0].Transaction.PostedDate);
        Assert.Equal(150m, register[0].RunningBalance);
        Assert.Equal(120m, register[1].RunningBalance);
    }

    [Fact]
    public async Task DeleteTransaction_RecalculatesBalance()
    {
        var account = await CreateCheckingAsync(100m);
        var txn = await _transactions.AddTransactionAsync(NewTxn(account.Id, -40m, new DateOnly(2026, 1, 5)));

        await _transactions.DeleteTransactionAsync(txn.Id);

        var reloaded = await _accounts.GetAccountAsync(account.Id);
        Assert.Equal(100m, reloaded!.CurrentBalance);
        Assert.Empty(await _transactions.GetRegisterAsync(account.Id));
    }

    [Fact]
    public async Task EditingSyncedAmount_SetsIsUserModified()
    {
        var account = await CreateCheckingAsync();
        var txn = NewTxn(account.Id, -10m, new DateOnly(2026, 1, 5));
        txn.Source = TransactionSource.SimpleFin;
        txn.ExternalId = "sf-txn-1";
        await _transactions.AddTransactionAsync(txn);

        txn.Amount = -12m;
        await _transactions.UpdateTransactionAsync(txn);

        var register = await _transactions.GetRegisterAsync(account.Id);
        Assert.True(register.Single().Transaction.IsUserModified);
    }

    [Fact]
    public async Task EditingSyncedPayeeOnly_DoesNotSetIsUserModified()
    {
        var account = await CreateCheckingAsync();
        var txn = NewTxn(account.Id, -10m, new DateOnly(2026, 1, 5));
        txn.Source = TransactionSource.SimpleFin;
        txn.ExternalId = "sf-txn-1";
        await _transactions.AddTransactionAsync(txn);

        txn.Payee = "Renamed";
        await _transactions.UpdateTransactionAsync(txn);

        var register = await _transactions.GetRegisterAsync(account.Id);
        Assert.False(register.Single().Transaction.IsUserModified);
        Assert.Equal("Renamed", register.Single().Transaction.Payee);
    }

    [Fact]
    public async Task EditingManualAmount_DoesNotSetIsUserModified()
    {
        var account = await CreateCheckingAsync();
        var txn = await _transactions.AddTransactionAsync(NewTxn(account.Id, -10m, new DateOnly(2026, 1, 5)));

        txn.Amount = -20m;
        await _transactions.UpdateTransactionAsync(txn);

        var register = await _transactions.GetRegisterAsync(account.Id);
        Assert.False(register.Single().Transaction.IsUserModified);
        Assert.Equal(-20m, register.Single().Transaction.Amount);
    }
    // --- the uncategorized worklist ---

    private async Task<int> AddCategoryAsync(string name)
    {
        await using var db = _db.CreateDbContext();
        var category = new Category { Name = name, CategoryType = CategoryType.Expense };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category.Id;
    }

    [Fact]
    public async Task GetUncategorizedAsync_ReturnsOnlyRowsWithNoCategory_NewestFirst()
    {
        var account = await CreateCheckingAsync();
        var groceries = await AddCategoryAsync("Groceries");

        await _transactions.AddTransactionAsync(NewTxn(account.Id, -5m, new DateOnly(2026, 8, 1), "older"));
        await _transactions.AddTransactionAsync(NewTxn(account.Id, -6m, new DateOnly(2026, 8, 20), "newer"));
        var filed = await _transactions.AddTransactionAsync(NewTxn(account.Id, -7m, new DateOnly(2026, 8, 25), "filed"));
        await _transactions.SetCategoryAsync(filed.Id, groceries);

        var rows = await _transactions.GetUncategorizedAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal("newer", rows[0].Payee);
        Assert.Equal("older", rows[1].Payee);
    }

    [Fact]
    public async Task GetUncategorizedAsync_FiltersByAccount()
    {
        var checking = await CreateCheckingAsync();
        var other = await _accounts.CreateAccountAsync(new Account
        {
            Name = "Savings", AccountType = AccountType.Savings, OpeningBalance = 0m,
        });
        await _transactions.AddTransactionAsync(NewTxn(checking.Id, -5m, new DateOnly(2026, 8, 1)));
        await _transactions.AddTransactionAsync(NewTxn(other.Id, -5m, new DateOnly(2026, 8, 1)));

        var rows = await _transactions.GetUncategorizedAsync(checking.Id);

        Assert.Equal(checking.Id, Assert.Single(rows).AccountId);
    }

    [Fact]
    public async Task CountUncategorizedAsync_CountsEveryAccount()
    {
        var account = await CreateCheckingAsync();
        await _transactions.AddTransactionAsync(NewTxn(account.Id, -5m, new DateOnly(2026, 8, 1)));
        await _transactions.AddTransactionAsync(NewTxn(account.Id, -6m, new DateOnly(2026, 8, 2)));

        Assert.Equal(2, await _transactions.CountUncategorizedAsync());
    }

    [Fact]
    public async Task SetCategoryAsync_AssignsAndClears()
    {
        var account = await CreateCheckingAsync();
        var groceries = await AddCategoryAsync("Groceries");
        var txn = await _transactions.AddTransactionAsync(NewTxn(account.Id, -5m, new DateOnly(2026, 8, 1)));

        await _transactions.SetCategoryAsync(txn.Id, groceries);
        Assert.Equal(groceries, (await _transactions.GetRegisterAsync(account.Id))[0].Transaction.CategoryId);

        await _transactions.SetCategoryAsync(txn.Id, null);
        Assert.Null((await _transactions.GetRegisterAsync(account.Id))[0].Transaction.CategoryId);
    }

    // Categorizing a synced row must not set IsUserModified: that flag guards Amount/PostedDate
    // against re-sync, and setting it here would wrongly freeze those fields too.
    [Fact]
    public async Task SetCategoryAsync_DoesNotMarkASyncedRowUserModified()
    {
        var account = await CreateCheckingAsync();
        var groceries = await AddCategoryAsync("Groceries");

        var txn = NewTxn(account.Id, -5m, new DateOnly(2026, 8, 1));
        txn.Source = TransactionSource.SimpleFin;
        txn.ExternalId = "TXN-1";
        var saved = await _transactions.AddTransactionAsync(txn);

        await _transactions.SetCategoryAsync(saved.Id, groceries);

        await using var db = _db.CreateDbContext();
        Assert.False(db.Transactions.First(t => t.Id == saved.Id).IsUserModified);
    }

    [Fact]
    public async Task SetCategoryAsync_LeavesTheBalanceAlone()
    {
        var account = await CreateCheckingAsync(opening: 100m);
        var groceries = await AddCategoryAsync("Groceries");
        var txn = await _transactions.AddTransactionAsync(NewTxn(account.Id, -25m, new DateOnly(2026, 8, 1)));

        await _transactions.SetCategoryAsync(txn.Id, groceries);

        Assert.Equal(75m, (await _accounts.GetAccountAsync(account.Id))!.CurrentBalance);
    }

}
