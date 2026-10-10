using Countinghouse.Domain.Entities;
using Countinghouse.Domain.Services;

namespace Countinghouse.Tests;

public class CategoryServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly CategoryService _categories;

    public CategoryServiceTests()
    {
        _categories = new CategoryService(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Create_RejectsDuplicateNameUnderSameParent()
    {
        var (first, _) = await _categories.CreateAsync("Groceries", CategoryType.Expense, null);
        Assert.NotNull(first);

        var (duplicate, error) = await _categories.CreateAsync("Groceries", CategoryType.Expense, null);
        Assert.Null(duplicate);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Create_AllowsSameNameUnderDifferentParents()
    {
        var (auto, _) = await _categories.CreateAsync("Auto", CategoryType.Expense, null);
        var (home, _) = await _categories.CreateAsync("Home", CategoryType.Expense, null);

        var (a, errA) = await _categories.CreateAsync("Insurance", CategoryType.Expense, auto!.Id);
        var (b, errB) = await _categories.CreateAsync("Insurance", CategoryType.Expense, home!.Id);

        Assert.Null(errA);
        Assert.Null(errB);
        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public async Task Delete_WithSubcategories_IsRefused()
    {
        var (parent, _) = await _categories.CreateAsync("Auto", CategoryType.Expense, null);
        await _categories.CreateAsync("Fuel", CategoryType.Expense, parent!.Id);

        var error = await _categories.DeleteAsync(parent.Id);
        Assert.NotNull(error);
        Assert.Equal(2, (await _categories.GetCategoriesAsync()).Count);
    }

    [Fact]
    public async Task Delete_LeavesTransactionsUncategorized()
    {
        var (category, _) = await _categories.CreateAsync("Groceries", CategoryType.Expense, null);

        int txnId;
        await using (var db = _db.CreateDbContext())
        {
            var account = new Account { Name = "Checking" };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            var txn = new Transaction
            {
                AccountId = account.Id,
                Amount = -50m,
                PostedDate = new DateOnly(2026, 1, 5),
                Payee = "Store",
                CategoryId = category!.Id,
            };
            db.Transactions.Add(txn);
            await db.SaveChangesAsync();
            txnId = txn.Id;
        }

        var error = await _categories.DeleteAsync(category!.Id);
        Assert.Null(error);

        await using var check = _db.CreateDbContext();
        var reloaded = await check.Transactions.FindAsync(txnId);
        Assert.Null(reloaded!.CategoryId);
    }
}
