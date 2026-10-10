using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Entities;
using Countinghouse.Domain.Services;

namespace Countinghouse.Tests;

public class CategoryRuleServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly CategoryRuleService _service;

    public CategoryRuleServiceTests() => _service = new CategoryRuleService(_db);

    public void Dispose() => _db.Dispose();

    private async Task<int> AddCategoryAsync(string name, CategoryType type = CategoryType.Expense)
    {
        await using var db = _db.CreateDbContext();
        var category = new Category { Name = name, CategoryType = type };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category.Id;
    }

    private async Task<int> AddAccountAsync()
    {
        await using var db = _db.CreateDbContext();
        var account = new Account { Name = "Checking", AccountType = AccountType.Checking };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task AddTransactionAsync(int accountId, string payee, int? categoryId, decimal amount = -10m)
    {
        await using var db = _db.CreateDbContext();
        db.Transactions.Add(new Transaction
        {
            AccountId = accountId,
            Payee = payee,
            CategoryId = categoryId,
            Amount = amount,
            PostedDate = new DateOnly(2026, 8, 1),
        });
        await db.SaveChangesAsync();
    }

    private async Task AddDatedAsync(int accountId, string payee, int? categoryId, DateOnly date)
    {
        await using var db = _db.CreateDbContext();
        db.Transactions.Add(new Transaction
        {
            AccountId = accountId, Payee = payee, CategoryId = categoryId,
            Amount = -10m, PostedDate = date,
        });
        await db.SaveChangesAsync();
    }

    private static CategoryRule Rule(int id, string pattern, CategoryRuleMatch match, int categoryId, bool enabled = true) =>
        new() { Id = id, Pattern = pattern, MatchType = match, CategoryId = categoryId, IsEnabled = enabled };

    // --- matching ---

    [Fact]
    public void ExactMatch_IsCaseAndWhitespaceInsensitive()
    {
        var rules = new[] { Rule(1, "Grocerly", CategoryRuleMatch.Exact, 7) };

        Assert.Equal(7, CategoryRuleService.FirstMatch(rules, "  gROCERLY ")?.CategoryId);
        Assert.Null(CategoryRuleService.FirstMatch(rules, "Grocerly #0123"));
    }

    [Fact]
    public void ContainsMatch_FindsADecoratedPayee()
    {
        var rules = new[] { Rule(1, "Corner Dairy", CategoryRuleMatch.Contains, 7) };

        Assert.Equal(7, CategoryRuleService.FirstMatch(rules, "Walletpay Corner Dairy")?.CategoryId);
    }

    // Exact is the more specific statement, so it must win regardless of insertion order.
    [Fact]
    public void ExactRule_BeatsAContainsRule()
    {
        var rules = new[]
        {
            Rule(1, "Grocerly", CategoryRuleMatch.Contains, 100),
            Rule(2, "Grocerly", CategoryRuleMatch.Exact, 200),
        };

        Assert.Equal(200, CategoryRuleService.FirstMatch(rules, "Grocerly")?.CategoryId);
    }

    // The ordering conflict that actually comes up, resolved without a priority column.
    [Fact]
    public void LongerPattern_WinsAmongContainsRules()
    {
        var rules = new[]
        {
            Rule(1, "Grocerly", CategoryRuleMatch.Contains, 100),
            Rule(2, "Grocerly Fuel", CategoryRuleMatch.Contains, 200),
        };

        Assert.Equal(200, CategoryRuleService.FirstMatch(rules, "Grocerly Fuel Center")?.CategoryId);
    }

    [Fact]
    public void DisabledRules_AreSkipped()
    {
        var rules = new[] { Rule(1, "Grocerly", CategoryRuleMatch.Exact, 7, enabled: false) };

        Assert.Null(CategoryRuleService.FirstMatch(rules, "Grocerly"));
    }

    [Fact]
    public void BlankPayee_MatchesNothing()
    {
        var rules = new[] { Rule(1, "Grocerly", CategoryRuleMatch.Contains, 7) };

        Assert.Null(CategoryRuleService.FirstMatch(rules, "   "));
    }

    // --- CRUD ---

    [Fact]
    public async Task CreateAsync_RejectsADuplicatePatternRegardlessOfCase()
    {
        var groceries = await AddCategoryAsync("Groceries");
        await _service.CreateAsync("Grocerly", CategoryRuleMatch.Exact, groceries);

        var (rule, error) = await _service.CreateAsync("grocerly", CategoryRuleMatch.Exact, groceries);

        Assert.Null(rule);
        Assert.Contains("already exists", error);
    }

    // The same pattern under a different match type is a different rule, not a duplicate.
    [Fact]
    public async Task CreateAsync_AllowsTheSamePatternWithADifferentMatchType()
    {
        var groceries = await AddCategoryAsync("Groceries");
        await _service.CreateAsync("Grocerly", CategoryRuleMatch.Exact, groceries);

        var (rule, error) = await _service.CreateAsync("Grocerly", CategoryRuleMatch.Contains, groceries);

        Assert.NotNull(rule);
        Assert.Null(error);
    }

    [Fact]
    public async Task CreateAsync_RejectsABlankPattern()
    {
        var groceries = await AddCategoryAsync("Groceries");

        var (rule, error) = await _service.CreateAsync("   ", CategoryRuleMatch.Exact, groceries);

        Assert.Null(rule);
        Assert.Contains("pattern is required", error);
    }

    // --- applying ---

    [Fact]
    public async Task ApplyAllAsync_FillsUncategorizedRows()
    {
        var groceries = await AddCategoryAsync("Groceries");
        var account = await AddAccountAsync();
        await AddTransactionAsync(account, "Grocerly", null);
        await AddTransactionAsync(account, "Grocerly", null);
        await _service.CreateAsync("Grocerly", CategoryRuleMatch.Exact, groceries);

        var result = await _service.ApplyAllAsync();

        Assert.Equal(2, result.Updated);
        await using var db = _db.CreateDbContext();
        Assert.All(await db.Transactions.ToListAsync(), t => Assert.Equal(groceries, t.CategoryId));
    }

    // The invariant that matters most: a category the user picked outranks any rule.
    [Fact]
    public async Task ApplyAllAsync_NeverOverwritesAnExistingCategory()
    {
        var groceries = await AddCategoryAsync("Groceries");
        var dining = await AddCategoryAsync("Dining");
        var account = await AddAccountAsync();
        await AddTransactionAsync(account, "Grocerly", dining);
        await _service.CreateAsync("Grocerly", CategoryRuleMatch.Exact, groceries);

        var result = await _service.ApplyAllAsync();

        Assert.Equal(0, result.Updated);
        await using var db = _db.CreateDbContext();
        Assert.Equal(dining, (await db.Transactions.FirstAsync()).CategoryId);
    }

    [Fact]
    public async Task ApplyAllAsync_CountsMatchedSeparatelyFromUpdated()
    {
        var groceries = await AddCategoryAsync("Groceries");
        var account = await AddAccountAsync();
        await AddTransactionAsync(account, "Grocerly", null);
        await AddTransactionAsync(account, "Some Payee No Rule Covers", null);
        await _service.CreateAsync("Grocerly", CategoryRuleMatch.Exact, groceries);

        var result = await _service.ApplyAllAsync();

        Assert.Equal(2, result.Matched);
        Assert.Equal(1, result.Updated);
    }

    [Fact]
    public async Task ApplyAllAsync_RecordsTimesApplied()
    {
        var groceries = await AddCategoryAsync("Groceries");
        var account = await AddAccountAsync();
        await AddTransactionAsync(account, "Grocerly", null);
        await AddTransactionAsync(account, "Grocerly", null);
        await _service.CreateAsync("Grocerly", CategoryRuleMatch.Exact, groceries);

        await _service.ApplyAllAsync();

        await using var db = _db.CreateDbContext();
        Assert.Equal(2, (await db.CategoryRules.FirstAsync()).TimesApplied);
    }

    [Fact]
    public async Task ApplyAllAsync_WithNoRules_DoesNothing()
    {
        var account = await AddAccountAsync();
        await AddTransactionAsync(account, "Grocerly", null);

        var result = await _service.ApplyAllAsync();

        Assert.Equal(0, result.Matched);
        Assert.Equal(0, result.Updated);
    }

    // --- suggestions ---

    [Fact]
    public async Task Suggest_PicksTheDominantHistoricalCategory()
    {
        var groceries = await AddCategoryAsync("Groceries");
        var dining = await AddCategoryAsync("Dining");
        var account = await AddAccountAsync();
        await AddTransactionAsync(account, "Grocerly", groceries);
        await AddTransactionAsync(account, "Grocerly", groceries);
        await AddTransactionAsync(account, "Grocerly", dining);
        await AddTransactionAsync(account, "Grocerly", null);

        var suggestion = Assert.Single(await _service.SuggestFromHistoryAsync());

        Assert.Equal("Grocerly", suggestion.Payee);
        Assert.Equal(groceries, suggestion.CategoryId);
        Assert.Equal(3, suggestion.HistoricalCount);
        Assert.Equal(2, suggestion.HistoricalDominantCount);
        Assert.Equal(1, suggestion.WouldCategorize);
        Assert.Equal(2.0 / 3.0, suggestion.Confidence, 3);
    }

    // A payee that stopped appearing years ago is history, not a suggestion — ordering by
    // historical volume alone would bury the ones that actually fix something.
    [Fact]
    public async Task Suggest_SkipsPayeesWithNothingLeftToCategorize()
    {
        var groceries = await AddCategoryAsync("Groceries");
        var account = await AddAccountAsync();
        await AddTransactionAsync(account, "Defunct Store", groceries);

        Assert.Empty(await _service.SuggestFromHistoryAsync());
    }

    [Fact]
    public async Task Suggest_SkipsPayeesAlreadyCoveredByARule()
    {
        var groceries = await AddCategoryAsync("Groceries");
        var account = await AddAccountAsync();
        await AddTransactionAsync(account, "Grocerly", groceries);
        await AddTransactionAsync(account, "Grocerly", null);
        await _service.CreateAsync("Grocerly", CategoryRuleMatch.Exact, groceries);

        Assert.Empty(await _service.SuggestFromHistoryAsync());
    }

    // Re-suggesting something a Contains rule already handles is the most annoying false positive.
    [Fact]
    public async Task Suggest_SkipsPayeesCoveredByAContainsRule()
    {
        var groceries = await AddCategoryAsync("Groceries");
        var account = await AddAccountAsync();
        await AddTransactionAsync(account, "Grocerly #0123", groceries);
        await AddTransactionAsync(account, "Grocerly #0123", null);
        await _service.CreateAsync("Grocerly", CategoryRuleMatch.Contains, groceries);

        Assert.Empty(await _service.SuggestFromHistoryAsync());
    }

    [Fact]
    public async Task Suggest_OrdersByWhatItWouldFix()
    {
        var groceries = await AddCategoryAsync("Groceries");
        var account = await AddAccountAsync();

        // Deep history, one row left to fix.
        for (var i = 0; i < 50; i++)
            await AddTransactionAsync(account, "Old Favourite", groceries);
        await AddTransactionAsync(account, "Old Favourite", null);

        // Shallow history, three rows to fix.
        await AddTransactionAsync(account, "New Regular", groceries);
        for (var i = 0; i < 3; i++)
            await AddTransactionAsync(account, "New Regular", null);

        var suggestions = await _service.SuggestFromHistoryAsync();

        Assert.Equal("New Regular", suggestions[0].Payee);
        Assert.Equal("Old Favourite", suggestions[1].Payee);
    }

    [Fact]
    public async Task Suggest_IsCaseInsensitiveAcrossHistoryAndPendingRows()
    {
        var groceries = await AddCategoryAsync("Groceries");
        var account = await AddAccountAsync();
        await AddTransactionAsync(account, "GROCERLY", groceries);
        await AddTransactionAsync(account, "grocerly", null);

        var suggestion = Assert.Single(await _service.SuggestFromHistoryAsync());

        Assert.Equal(1, suggestion.WouldCategorize);
    }
    // --- recency weighting ---

    private static readonly DateOnly Today = new(2026, 8, 27);

    // A payee whose filing has drifted: dominant across all of history, but recent filing has
    // moved on. All-time alone would recommend a rule that's now wrong most of the time, so the
    // recent window has to win.
    [Fact]
    public async Task Suggest_PrefersRecentFilingOverTheLongRunPattern()
    {
        var groceries = await AddCategoryAsync("Groceries");
        var medicine = await AddCategoryAsync("Medicine");
        var account = await AddAccountAsync();

        // Long tail, all Groceries, well outside the window.
        for (var i = 0; i < 40; i++)
            await AddDatedAsync(account, "Cornerpharm", groceries, new DateOnly(2018, 1, 1).AddDays(i));

        // Recent behaviour: mostly Medicine.
        for (var i = 0; i < 8; i++)
            await AddDatedAsync(account, "Cornerpharm", medicine, new DateOnly(2026, 1, 1).AddDays(i));
        await AddDatedAsync(account, "Cornerpharm", groceries, new DateOnly(2026, 2, 1));
        await AddDatedAsync(account, "Cornerpharm", null, new DateOnly(2026, 8, 1));

        var suggestion = Assert.Single(await _service.SuggestFromHistoryAsync(Today));

        Assert.Equal(medicine, suggestion.CategoryId);
        Assert.True(suggestion.BasedOnRecent);
        Assert.Equal(9, suggestion.RecentCount);
        Assert.Equal(8, suggestion.RecentDominantCount);
        Assert.Equal(8.0 / 9.0, suggestion.Confidence, 3);
    }

    [Fact]
    public async Task Suggest_FlagsWhenRecentFilingDivergesFromHistory()
    {
        var groceries = await AddCategoryAsync("Groceries");
        var medicine = await AddCategoryAsync("Medicine");
        var account = await AddAccountAsync();

        for (var i = 0; i < 40; i++)
            await AddDatedAsync(account, "Cornerpharm", groceries, new DateOnly(2018, 1, 1).AddDays(i));
        for (var i = 0; i < 8; i++)
            await AddDatedAsync(account, "Cornerpharm", medicine, new DateOnly(2026, 1, 1).AddDays(i));
        await AddDatedAsync(account, "Cornerpharm", null, new DateOnly(2026, 8, 1));

        var suggestion = Assert.Single(await _service.SuggestFromHistoryAsync(Today));

        Assert.True(suggestion.DivergesFromHistory);
        // All-time is dominated by the old Groceries filing; recent is not.
        Assert.Equal(8.0 / 48.0, suggestion.AllTimeConfidence, 3);
    }

    [Fact]
    public async Task Suggest_DoesNotFlagDivergenceWhenFilingIsStable()
    {
        var groceries = await AddCategoryAsync("Groceries");
        var account = await AddAccountAsync();

        for (var i = 0; i < 20; i++)
            await AddDatedAsync(account, "Grocerly", groceries, new DateOnly(2018, 1, 1).AddDays(i));
        for (var i = 0; i < 10; i++)
            await AddDatedAsync(account, "Grocerly", groceries, new DateOnly(2026, 1, 1).AddDays(i));
        await AddDatedAsync(account, "Grocerly", null, new DateOnly(2026, 8, 1));

        var suggestion = Assert.Single(await _service.SuggestFromHistoryAsync(Today));

        Assert.True(suggestion.BasedOnRecent);
        Assert.False(suggestion.DivergesFromHistory);
        Assert.Equal(1.0, suggestion.Confidence, 3);
    }

    // Too few recent rows is not evidence of a change of habit — fall back rather than let two
    // transactions overrule twenty years.
    [Fact]
    public async Task Suggest_FallsBackToAllHistoryWhenTheRecentWindowIsThin()
    {
        var groceries = await AddCategoryAsync("Groceries");
        var dining = await AddCategoryAsync("Dining");
        var account = await AddAccountAsync();

        for (var i = 0; i < 30; i++)
            await AddDatedAsync(account, "Somewhere", groceries, new DateOnly(2018, 1, 1).AddDays(i));
        await AddDatedAsync(account, "Somewhere", dining, new DateOnly(2026, 1, 1));
        await AddDatedAsync(account, "Somewhere", null, new DateOnly(2026, 8, 1));

        var suggestion = Assert.Single(await _service.SuggestFromHistoryAsync(Today));

        Assert.False(suggestion.BasedOnRecent);
        Assert.Equal(groceries, suggestion.CategoryId);
        Assert.False(suggestion.DivergesFromHistory);
    }

}
