using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Entities;
using Countinghouse.Domain.Services;

namespace Countinghouse.Tests;

public class ReconciliationServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AccountService _accounts;
    private readonly TransactionService _transactions;
    private readonly ReconciliationService _reconciliation;

    public ReconciliationServiceTests()
    {
        _accounts = new AccountService(_db);
        _transactions = new TransactionService(_db);
        _reconciliation = new ReconciliationService(_db);
    }

    public void Dispose() => _db.Dispose();

    private async Task<Account> CreateAccountAsync(
        decimal opening = 1000m,
        AccountType type = AccountType.Checking,
        DateOnly? openingDate = null,
        bool? syncOverride = null,
        string name = "Checking") =>
        await _accounts.CreateAccountAsync(new Account
        {
            Name = name,
            AccountType = type,
            OpeningBalance = opening,
            OpeningBalanceDate = openingDate,
            SyncTransactionsOverride = syncOverride,
        });

    private static Transaction NewTxn(
        int accountId, decimal amount, DateOnly date, ClearedStatus status = ClearedStatus.Uncleared, string payee = "Test") =>
        new()
        {
            AccountId = accountId,
            Amount = amount,
            PostedDate = date,
            Payee = payee,
            Source = TransactionSource.Manual,
            ClearedStatus = status,
        };

    private async Task<Transaction> AddTxnAsync(
        int accountId, decimal amount, DateOnly date, ClearedStatus status = ClearedStatus.Uncleared, string payee = "Test") =>
        await _transactions.AddTransactionAsync(NewTxn(accountId, amount, date, status, payee));

    private async Task<ReconciliationSession> InsertSessionDirectAsync(
        int accountId, DateOnly startDate, DateOnly endDate, decimal startingBalance, decimal endingBalance, bool isComplete)
    {
        await using var db = _db.CreateDbContext();
        var session = new ReconciliationSession
        {
            AccountId = accountId,
            StatementStartDate = startDate,
            StatementEndDate = endDate,
            StatementStartingBalance = startingBalance,
            StatementEndingBalance = endingBalance,
            IsComplete = isComplete,
            CompletedAt = isComplete ? DateTime.Now : null,
        };
        db.ReconciliationSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private async Task<Transaction> ReloadTxnAsync(int id)
    {
        await using var db = _db.CreateDbContext();
        return await db.Transactions.AsNoTracking().Include(t => t.Category).FirstAsync(t => t.Id == id);
    }

    private async Task<ReconciliationSession> ReloadSessionAsync(int id)
    {
        await using var db = _db.CreateDbContext();
        return await db.ReconciliationSessions.AsNoTracking().FirstAsync(s => s.Id == id);
    }

    // ---------- GetSuggestionAsync ----------

    [Fact]
    public async Task GetSuggestion_FirstSession_StartingBalanceIsOpeningPlusReconciledOnly()
    {
        var account = await CreateAccountAsync(opening: 1000m, openingDate: new DateOnly(2026, 1, 1));
        await AddTxnAsync(account.Id, 50m, new DateOnly(2026, 1, 5), ClearedStatus.Reconciled);
        await AddTxnAsync(account.Id, 20m, new DateOnly(2026, 1, 6), ClearedStatus.Cleared);
        await AddTxnAsync(account.Id, 10m, new DateOnly(2026, 1, 7), ClearedStatus.Uncleared);

        var suggestion = await _reconciliation.GetSuggestionAsync(account.Id);

        Assert.Equal(1050m, suggestion.StartingBalance);
        Assert.True(suggestion.IsFirstSession);
        // Earliest non-reconciled row, not the opening balance date.
        Assert.Equal(new DateOnly(2026, 1, 6), suggestion.StartDate);
        Assert.Equal(new DateOnly(2026, 2, 5), suggestion.EndDate);
    }

    [Fact]
    public async Task GetSuggestion_WithPriorSession_UsesItsEndingBalanceAndDayAfter()
    {
        var account = await CreateAccountAsync();
        await InsertSessionDirectAsync(
            account.Id, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), 2000m, 2500m, isComplete: true);

        var suggestion = await _reconciliation.GetSuggestionAsync(account.Id);

        Assert.Equal(2500m, suggestion.StartingBalance);
        Assert.Equal(new DateOnly(2026, 4, 1), suggestion.StartDate);
        Assert.Equal(new DateOnly(2026, 4, 30), suggestion.EndDate);
        Assert.False(suggestion.IsFirstSession);
    }

    [Fact]
    public async Task GetSuggestion_MultipleCompletedSessions_PicksLatestAndIgnoresIncomplete()
    {
        var account = await CreateAccountAsync();
        await InsertSessionDirectAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 900m, 1000m, isComplete: true);
        await InsertSessionDirectAsync(
            account.Id, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), 1000m, 1200m, isComplete: true);
        // Later statement end date but incomplete — must be ignored.
        await InsertSessionDirectAsync(
            account.Id, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), 1200m, 9999m, isComplete: false);

        var suggestion = await _reconciliation.GetSuggestionAsync(account.Id);

        Assert.Equal(1200m, suggestion.StartingBalance);
        Assert.Equal(new DateOnly(2026, 3, 1), suggestion.StartDate);
        Assert.False(suggestion.IsFirstSession);
    }

    [Fact]
    public async Task GetSuggestion_FirstSession_NaturalEndInFuture_ClampsToToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var account = await CreateAccountAsync(openingDate: today.AddDays(-90));
        await AddTxnAsync(account.Id, -50m, today.AddDays(-3));

        var suggestion = await _reconciliation.GetSuggestionAsync(account.Id);

        // The natural window is a month from the first unreconciled row, which lands in the future.
        Assert.Equal(today.AddDays(-3), suggestion.StartDate);
        Assert.Equal(today, suggestion.EndDate);
    }

    [Fact]
    public async Task GetSuggestion_WithPriorSession_NaturalEndInFuture_ClampsToToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var account = await CreateAccountAsync();
        await InsertSessionDirectAsync(
            account.Id, today.AddDays(-31), today.AddDays(-1), 2000m, 2500m, isComplete: true);

        var suggestion = await _reconciliation.GetSuggestionAsync(account.Id);

        Assert.Equal(today, suggestion.StartDate);
        Assert.Equal(today, suggestion.EndDate);
    }

    [Fact]
    public async Task GetSuggestion_ClampedEnd_IsAcceptedByStartSession()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var account = await CreateAccountAsync(openingDate: today.AddDays(-90));
        await AddTxnAsync(account.Id, -50m, today.AddDays(-3));

        // The suggestion must never propose dates its own guard would refuse — that combination
        // is what produced a statement period running a month into the future in the first place.
        var suggestion = await _reconciliation.GetSuggestionAsync(account.Id);
        var session = await _reconciliation.StartSessionAsync(
            account.Id, suggestion.StartDate, suggestion.EndDate, suggestion.StartingBalance, 950m);

        Assert.Equal(suggestion.EndDate, session.StatementEndDate);
    }

    // ---------- StartSessionAsync ----------

    [Fact]
    public async Task StartSession_CreatesIncompleteSessionWithGivenValues()
    {
        var account = await CreateAccountAsync();

        var session = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1200m);

        Assert.True(session.Id > 0);
        Assert.Equal(account.Id, session.AccountId);
        Assert.Equal(new DateOnly(2026, 1, 1), session.StatementStartDate);
        Assert.Equal(new DateOnly(2026, 1, 31), session.StatementEndDate);
        Assert.Equal(1000m, session.StatementStartingBalance);
        Assert.Equal(1200m, session.StatementEndingBalance);
        Assert.False(session.IsComplete);
        Assert.Null(session.CompletedAt);
    }

    [Fact]
    public async Task StartSession_EndBeforeStart_Throws()
    {
        var account = await CreateAccountAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _reconciliation.StartSessionAsync(
                account.Id, new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1), 1000m, 1200m));
    }

    [Fact]
    public async Task StartSession_EndDateInFuture_Throws()
    {
        var account = await CreateAccountAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);

        // A statement covering a period that hasn't closed would lock every transaction arriving
        // before that date into it, leaving nothing to tick against the statement that lists them.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _reconciliation.StartSessionAsync(
                account.Id, today.AddDays(-30), today.AddDays(1), 1000m, 1200m));
    }

    [Fact]
    public async Task StartSession_EndDateToday_DoesNotThrow()
    {
        var account = await CreateAccountAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);

        // Today is the boundary and belongs on the allowed side: a statement can close today.
        var session = await _reconciliation.StartSessionAsync(
            account.Id, today.AddDays(-30), today, 1000m, 1200m);

        Assert.Equal(today, session.StatementEndDate);
    }

    [Fact]
    public async Task StartSession_ExistingIncompleteSession_Throws()
    {
        var account = await CreateAccountAsync();
        await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1200m);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _reconciliation.StartSessionAsync(
                account.Id, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), 1200m, 1300m));
    }

    [Fact]
    public async Task StartSession_CompletedExistingOrDifferentAccountIncomplete_DoesNotThrow()
    {
        var accountA = await CreateAccountAsync(name: "A");
        var firstSession = await _reconciliation.StartSessionAsync(
            accountA.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1000m);
        await _reconciliation.FinishAsync(firstSession.Id);

        // Same account, but the only existing session is now complete.
        var secondSession = await _reconciliation.StartSessionAsync(
            accountA.Id, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), 1000m, 1100m);
        Assert.False(secondSession.IsComplete);

        // A different account's incomplete session must not block accountB.
        var accountB = await CreateAccountAsync(name: "B");
        var thirdSession = await _reconciliation.StartSessionAsync(
            accountB.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 500m, 600m);
        Assert.False(thirdSession.IsComplete);
    }

    [Fact]
    public async Task StartSession_BalanceOnlyAccount_ThrowsUnlessOverridden()
    {
        var investment = await CreateAccountAsync(type: AccountType.Investment, name: "401k");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _reconciliation.StartSessionAsync(
                investment.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1000m));

        var overridden = await CreateAccountAsync(type: AccountType.Investment, syncOverride: true, name: "Brokerage");
        var session = await _reconciliation.StartSessionAsync(
            overridden.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1000m);
        Assert.False(session.IsComplete);
    }

    // ---------- GetWorksheetAsync ----------

    [Fact]
    public async Task GetWorksheet_UnknownSessionId_ReturnsNull()
    {
        Assert.Null(await _reconciliation.GetWorksheetAsync(999999));
    }

    [Fact]
    public async Task GetWorksheet_IncludesUnclearedAndCleared_ExcludesReconciledAfterEndDateAndOtherAccounts()
    {
        var account = await CreateAccountAsync(name: "A");
        var session = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1000m);

        var uncleared = await AddTxnAsync(account.Id, 5m, new DateOnly(2026, 1, 10), ClearedStatus.Uncleared);
        var cleared = await AddTxnAsync(account.Id, 15m, new DateOnly(2026, 1, 15), ClearedStatus.Cleared);
        await AddTxnAsync(account.Id, 25m, new DateOnly(2026, 1, 5), ClearedStatus.Reconciled);
        await AddTxnAsync(account.Id, 30m, new DateOnly(2026, 2, 5), ClearedStatus.Uncleared);

        var other = await CreateAccountAsync(name: "B");
        await AddTxnAsync(other.Id, 40m, new DateOnly(2026, 1, 10), ClearedStatus.Uncleared);

        var worksheet = await _reconciliation.GetWorksheetAsync(session.Id);

        Assert.NotNull(worksheet);
        var ids = worksheet!.Rows.Select(r => r.Id).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Contains(uncleared.Id, ids);
        Assert.Contains(cleared.Id, ids);
    }

    [Fact]
    public async Task GetWorksheet_IncludesOutstandingCheckBeforeStatementStart()
    {
        var account = await CreateAccountAsync();
        var session = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), 1000m, 1000m);

        var outstandingCheck = await AddTxnAsync(account.Id, -100m, new DateOnly(2026, 1, 15), ClearedStatus.Uncleared);

        var worksheet = await _reconciliation.GetWorksheetAsync(session.Id);

        Assert.NotNull(worksheet);
        Assert.Contains(worksheet!.Rows, r => r.Id == outstandingCheck.Id);
    }

    [Fact]
    public async Task GetWorksheet_ClearedBalanceDifferenceCount_AndIsBalancedTrue()
    {
        var account = await CreateAccountAsync();
        var session = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1030m);

        await AddTxnAsync(account.Id, 50m, new DateOnly(2026, 1, 10), ClearedStatus.Cleared);
        await AddTxnAsync(account.Id, -20m, new DateOnly(2026, 1, 15), ClearedStatus.Cleared);
        await AddTxnAsync(account.Id, 1000m, new DateOnly(2026, 1, 20), ClearedStatus.Uncleared);

        var worksheet = await _reconciliation.GetWorksheetAsync(session.Id);

        Assert.NotNull(worksheet);
        Assert.Equal(1030m, worksheet!.ClearedBalance);
        Assert.Equal(0m, worksheet.Difference);
        Assert.Equal(2, worksheet.ClearedCount);
        Assert.True(worksheet.IsBalanced);
    }

    [Fact]
    public async Task GetWorksheet_IsBalancedFalse_AndReconciledThroughBalanceDrift()
    {
        var account = await CreateAccountAsync(opening: 1000m);
        // Prior reconciled history the statement's starting balance disagrees with.
        await AddTxnAsync(account.Id, 25m, new DateOnly(2025, 12, 15), ClearedStatus.Reconciled);

        var session = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1050m);
        await AddTxnAsync(account.Id, 10m, new DateOnly(2026, 1, 10), ClearedStatus.Cleared);

        var worksheet = await _reconciliation.GetWorksheetAsync(session.Id);

        Assert.NotNull(worksheet);
        Assert.Equal(1010m, worksheet!.ClearedBalance);
        Assert.Equal(40m, worksheet.Difference);
        Assert.False(worksheet.IsBalanced);
        Assert.Equal(1025m, worksheet.ReconciledThroughBalance);
        Assert.Equal(-25m, worksheet.StartingBalanceDrift);
    }

    [Fact]
    public async Task GetWorksheet_CreditCardNegativeAmounts_ReconcilesToZero()
    {
        var account = await CreateAccountAsync(opening: -500m, type: AccountType.CreditCard);
        var session = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), -500m, -650m);

        await AddTxnAsync(account.Id, -200m, new DateOnly(2026, 1, 10), ClearedStatus.Cleared, "Purchase");
        await AddTxnAsync(account.Id, 50m, new DateOnly(2026, 1, 20), ClearedStatus.Cleared, "Payment");

        var worksheet = await _reconciliation.GetWorksheetAsync(session.Id);

        Assert.NotNull(worksheet);
        Assert.Equal(-650m, worksheet!.ClearedBalance);
        Assert.Equal(0m, worksheet.Difference);
        Assert.True(worksheet.IsBalanced);
    }

    // ---------- SetClearedAsync / SetAllClearedAsync ----------

    [Fact]
    public async Task SetCleared_TogglesUnclearedToClearedAndBack()
    {
        var account = await CreateAccountAsync();
        var txn = await AddTxnAsync(account.Id, 10m, new DateOnly(2026, 1, 5), ClearedStatus.Uncleared);

        await _reconciliation.SetClearedAsync(txn.Id, true);
        Assert.Equal(ClearedStatus.Cleared, (await ReloadTxnAsync(txn.Id)).ClearedStatus);

        await _reconciliation.SetClearedAsync(txn.Id, false);
        Assert.Equal(ClearedStatus.Uncleared, (await ReloadTxnAsync(txn.Id)).ClearedStatus);
    }

    [Fact]
    public async Task SetCleared_OnReconciledRow_IsNoOp()
    {
        var account = await CreateAccountAsync();
        var txn = await AddTxnAsync(account.Id, 10m, new DateOnly(2026, 1, 5), ClearedStatus.Reconciled);

        await _reconciliation.SetClearedAsync(txn.Id, false);
        Assert.Equal(ClearedStatus.Reconciled, (await ReloadTxnAsync(txn.Id)).ClearedStatus);

        await _reconciliation.SetClearedAsync(txn.Id, true);
        Assert.Equal(ClearedStatus.Reconciled, (await ReloadTxnAsync(txn.Id)).ClearedStatus);
    }

    [Fact]
    public async Task SetCleared_UnknownTransactionId_DoesNotThrow()
    {
        await _reconciliation.SetClearedAsync(999999, true);
    }

    [Fact]
    public async Task SetAllCleared_AffectsOnlySessionAccountRowsInWindow_NeverReconciled()
    {
        var account = await CreateAccountAsync(name: "A");
        var session = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1000m);

        var inWindow = await AddTxnAsync(account.Id, 5m, new DateOnly(2026, 1, 10), ClearedStatus.Uncleared);
        var reconciled = await AddTxnAsync(account.Id, 25m, new DateOnly(2026, 1, 5), ClearedStatus.Reconciled);
        var afterWindow = await AddTxnAsync(account.Id, 30m, new DateOnly(2026, 2, 5), ClearedStatus.Uncleared);

        var other = await CreateAccountAsync(name: "B");
        var otherTxn = await AddTxnAsync(other.Id, 40m, new DateOnly(2026, 1, 10), ClearedStatus.Uncleared);

        await _reconciliation.SetAllClearedAsync(session.Id, true);

        Assert.Equal(ClearedStatus.Cleared, (await ReloadTxnAsync(inWindow.Id)).ClearedStatus);
        Assert.Equal(ClearedStatus.Reconciled, (await ReloadTxnAsync(reconciled.Id)).ClearedStatus);
        Assert.Equal(ClearedStatus.Uncleared, (await ReloadTxnAsync(afterWindow.Id)).ClearedStatus);
        Assert.Equal(ClearedStatus.Uncleared, (await ReloadTxnAsync(otherTxn.Id)).ClearedStatus);

        await _reconciliation.SetAllClearedAsync(session.Id, false);
        Assert.Equal(ClearedStatus.Uncleared, (await ReloadTxnAsync(inWindow.Id)).ClearedStatus);
        Assert.Equal(ClearedStatus.Reconciled, (await ReloadTxnAsync(reconciled.Id)).ClearedStatus);
    }

    // ---------- FinishAsync ----------

    [Fact]
    public async Task Finish_Balanced_ReconcilesClearedRowsAndLeavesUnclearedAlone_NoAdjustment()
    {
        var account = await CreateAccountAsync();
        var session = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1030m);

        var cleared1 = await AddTxnAsync(account.Id, 20m, new DateOnly(2026, 1, 10), ClearedStatus.Cleared);
        var cleared2 = await AddTxnAsync(account.Id, 10m, new DateOnly(2026, 1, 15), ClearedStatus.Cleared);
        var uncleared = await AddTxnAsync(account.Id, 5m, new DateOnly(2026, 1, 20), ClearedStatus.Uncleared);

        var result = await _reconciliation.FinishAsync(session.Id);

        Assert.Equal(2, result.ReconciledCount);
        Assert.Equal(0m, result.AdjustmentAmount);

        var reloaded1 = await ReloadTxnAsync(cleared1.Id);
        Assert.Equal(ClearedStatus.Reconciled, reloaded1.ClearedStatus);
        Assert.Equal(session.Id, reloaded1.ReconciliationSessionId);

        var reloaded2 = await ReloadTxnAsync(cleared2.Id);
        Assert.Equal(ClearedStatus.Reconciled, reloaded2.ClearedStatus);

        var reloadedUncleared = await ReloadTxnAsync(uncleared.Id);
        Assert.Equal(ClearedStatus.Uncleared, reloadedUncleared.ClearedStatus);
        Assert.Null(reloadedUncleared.ReconciliationSessionId);

        var reloadedSession = await ReloadSessionAsync(session.Id);
        Assert.True(reloadedSession.IsComplete);
        Assert.NotNull(reloadedSession.CompletedAt);

        // No adjustment row: only the three original transactions exist.
        Assert.Equal(3, (await _transactions.GetRegisterAsync(account.Id)).Count);
    }

    [Fact]
    public async Task Finish_Unbalanced_NoAdjustment_Throws_NothingMutated()
    {
        var account = await CreateAccountAsync();
        var session = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1030m);
        var cleared = await AddTxnAsync(account.Id, 10m, new DateOnly(2026, 1, 10), ClearedStatus.Cleared);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _reconciliation.FinishAsync(session.Id));

        var reloadedSession = await ReloadSessionAsync(session.Id);
        Assert.False(reloadedSession.IsComplete);

        var reloadedTxn = await ReloadTxnAsync(cleared.Id);
        Assert.Equal(ClearedStatus.Cleared, reloadedTxn.ClearedStatus);
        Assert.Null(reloadedTxn.ReconciliationSessionId);
    }

    [Fact]
    public async Task Finish_Unbalanced_WithAdjustment_PostsAdjustmentAndUpdatesManualAccountBalance()
    {
        var account = await CreateAccountAsync(opening: 1000m);
        var session = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1030m);
        await AddTxnAsync(account.Id, 10m, new DateOnly(2026, 1, 10), ClearedStatus.Cleared);

        var result = await _reconciliation.FinishAsync(session.Id, postAdjustment: true);

        Assert.Equal(1, result.ReconciledCount);
        Assert.Equal(20m, result.AdjustmentAmount);

        var register = await _transactions.GetRegisterAsync(account.Id);
        Assert.Equal(2, register.Count);
        var adjustment = register.Single(r => r.Transaction.Payee == AccountService.AdjustmentCategoryName).Transaction;
        Assert.Equal(20m, adjustment.Amount);
        Assert.Equal(new DateOnly(2026, 1, 31), adjustment.PostedDate);
        Assert.Equal(ClearedStatus.Reconciled, adjustment.ClearedStatus);
        Assert.Equal(session.Id, adjustment.ReconciliationSessionId);
        Assert.NotNull(adjustment.Category);
        Assert.Equal(AccountService.AdjustmentCategoryName, adjustment.Category!.Name);
        Assert.Equal(CategoryType.Transfer, adjustment.Category.CategoryType);

        var reloadedAccount = await _accounts.GetAccountAsync(account.Id);
        Assert.Equal(1030m, reloadedAccount!.CurrentBalance);
    }

    [Fact]
    public async Task Finish_AlreadyComplete_Throws()
    {
        var account = await CreateAccountAsync();
        var session = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1000m);
        await _reconciliation.FinishAsync(session.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _reconciliation.FinishAsync(session.Id));
    }

    [Fact]
    public async Task Finish_SecondSession_OnlySeesRowsLeftByFirst()
    {
        var account = await CreateAccountAsync();
        var firstSession = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1020m);
        await AddTxnAsync(account.Id, 20m, new DateOnly(2026, 1, 10), ClearedStatus.Cleared);
        await _reconciliation.FinishAsync(firstSession.Id);

        var secondSession = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), 1020m, 1035m);
        var newTxn = await AddTxnAsync(account.Id, 15m, new DateOnly(2026, 2, 10), ClearedStatus.Cleared);

        var worksheet = await _reconciliation.GetWorksheetAsync(secondSession.Id);

        Assert.NotNull(worksheet);
        var ids = worksheet!.Rows.Select(r => r.Id).ToList();
        Assert.Single(ids);
        Assert.Contains(newTxn.Id, ids);
    }

    // ---------- CancelSessionAsync ----------

    [Fact]
    public async Task CancelSession_RemovesIncompleteSession_AllowsNewSession_DoesNotChangeClearedStatus()
    {
        var account = await CreateAccountAsync();
        var session = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1000m);
        var txn = await AddTxnAsync(account.Id, 10m, new DateOnly(2026, 1, 10), ClearedStatus.Cleared);

        await _reconciliation.CancelSessionAsync(session.Id);

        Assert.Null(await _reconciliation.GetOpenSessionAsync(account.Id));
        Assert.Equal(ClearedStatus.Cleared, (await ReloadTxnAsync(txn.Id)).ClearedStatus);

        var newSession = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1010m);
        Assert.False(newSession.IsComplete);
    }

    [Fact]
    public async Task CancelSession_OnCompletedOrUnknownSession_IsNoOp()
    {
        var account = await CreateAccountAsync();
        var session = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1000m);
        await _reconciliation.FinishAsync(session.Id);

        await _reconciliation.CancelSessionAsync(session.Id);
        Assert.Single(await _reconciliation.GetHistoryAsync(account.Id));

        await _reconciliation.CancelSessionAsync(999999);
    }

    // ---------- GetOpenSessionAsync / GetHistoryAsync ----------

    [Fact]
    public async Task GetOpenSession_ReturnsIncompleteSessionOrNull()
    {
        var account = await CreateAccountAsync();
        Assert.Null(await _reconciliation.GetOpenSessionAsync(account.Id));

        var session = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1000m);

        var open = await _reconciliation.GetOpenSessionAsync(account.Id);
        Assert.NotNull(open);
        Assert.Equal(session.Id, open!.Id);
    }

    [Fact]
    public async Task GetHistory_ReturnsOnlyCompletedNewestFirst()
    {
        var account = await CreateAccountAsync();
        var first = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1000m, 1000m);
        await _reconciliation.FinishAsync(first.Id);

        var second = await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), 1000m, 1000m);
        await _reconciliation.FinishAsync(second.Id);

        // Left incomplete on purpose — must not appear in history.
        await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), 1000m, 1100m);

        var history = await _reconciliation.GetHistoryAsync(account.Id);

        Assert.Equal(2, history.Count);
        Assert.Equal(second.Id, history[0].Id);
        Assert.Equal(first.Id, history[1].Id);
    }

    // ---------- ReconcileAgainstSyncedBalanceAsync ----------

    // Seeded directly rather than through AccountService, which stamps its own CurrentBalance and
    // BalanceAsOf — on a synced account those two are the bank's figures, not ours.
    private async Task<Account> SeedSyncedAccountAsync(
        decimal openingBalance = 0m,
        decimal syncedBalance = 0m,
        DateTime? balanceAsOf = null,
        bool hasSynced = true,
        bool isSynced = true,
        bool? syncTransactionsOverride = null)
    {
        await using var db = _db.CreateDbContext();
        var connection = new SimpleFinConnection { InstitutionLabel = "Bank", EncryptedAccessUrl = "cipher" };
        db.SimpleFinConnections.Add(connection);
        await db.SaveChangesAsync();

        var account = new Account
        {
            Name = "Synced Checking",
            AccountType = AccountType.Checking,
            OpeningBalance = openingBalance,
            OpeningBalanceDate = new DateOnly(2026, 1, 1),
            CurrentBalance = syncedBalance,
            BalanceAsOf = hasSynced ? balanceAsOf ?? new DateTime(2026, 8, 26, 9, 0, 0) : null,
            SimpleFinConnectionId = isSynced ? connection.Id : null,
            SimpleFinAccountId = isSynced ? "remote-1" : null,
            SyncTransactionsOverride = syncTransactionsOverride,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    // Added straight to the context: BalanceCalculator no-ops for synced accounts anyway, and the
    // seeded CurrentBalance must stay exactly what the bank reported.
    private async Task AddSyncedRowAsync(
        int accountId,
        decimal amount,
        DateOnly date,
        ClearedStatus status = ClearedStatus.Cleared,
        bool pending = false)
    {
        await using var db = _db.CreateDbContext();
        db.Transactions.Add(new Transaction
        {
            AccountId = accountId,
            Amount = amount,
            PostedDate = date,
            Payee = "synced row",
            Pending = pending,
            ClearedStatus = status,
            Source = TransactionSource.SimpleFin,
            ExternalId = Guid.NewGuid().ToString(),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ReconcileAgainstSyncedBalance_RegisterAddsUp_FinishesInOneStep()
    {
        var account = await SeedSyncedAccountAsync(openingBalance: 1000m, syncedBalance: 1120m);
        await AddSyncedRowAsync(account.Id, 100m, new DateOnly(2026, 8, 20));
        await AddSyncedRowAsync(account.Id, 20m, new DateOnly(2026, 8, 25));

        var result = await _reconciliation.ReconcileAgainstSyncedBalanceAsync(account.Id);

        Assert.True(result.Finished);
        Assert.Equal(2, result.ReconciledCount);
        Assert.Equal(0m, result.Difference);

        var session = await ReloadSessionAsync(result.Session.Id);
        Assert.True(session.IsComplete);
        // End date is the bank's own timestamp, and the ending balance is its figure verbatim.
        Assert.Equal(new DateOnly(2026, 8, 26), session.StatementEndDate);
        Assert.Equal(1120m, session.StatementEndingBalance);

        await using var db = _db.CreateDbContext();
        var rows = await db.Transactions.AsNoTracking().Where(t => t.AccountId == account.Id).ToListAsync();
        Assert.All(rows, t => Assert.Equal(ClearedStatus.Reconciled, t.ClearedStatus));
        Assert.All(rows, t => Assert.Equal(session.Id, t.ReconciliationSessionId));
    }

    [Fact]
    public async Task ReconcileAgainstSyncedBalance_Off_LeavesSessionOpenAndReconcilesNothing()
    {
        // The register is short $75 — what a silently dropped transaction looks like.
        var account = await SeedSyncedAccountAsync(openingBalance: 1000m, syncedBalance: 1175m);
        await AddSyncedRowAsync(account.Id, 100m, new DateOnly(2026, 8, 20));

        var result = await _reconciliation.ReconcileAgainstSyncedBalanceAsync(account.Id);

        Assert.False(result.Finished);
        Assert.Equal(0, result.ReconciledCount);
        Assert.Equal(75m, result.Difference);

        var session = await ReloadSessionAsync(result.Session.Id);
        Assert.False(session.IsComplete);

        await using var db = _db.CreateDbContext();
        var rows = await db.Transactions.AsNoTracking().Where(t => t.AccountId == account.Id).ToListAsync();
        // No adjustment was posted and no row was locked: the evidence is left intact.
        Assert.Single(rows);
        Assert.Equal(ClearedStatus.Cleared, rows[0].ClearedStatus);
    }

    [Fact]
    public async Task ReconcileAgainstSyncedBalance_PendingRow_IsExcludedAndStillBalances()
    {
        // SimpleFIN's balance is the posted balance, and a pending row arrives Uncleared — so it
        // sits in the worksheet unticked and outside the cleared total on its own.
        var account = await SeedSyncedAccountAsync(openingBalance: 1000m, syncedBalance: 1100m);
        await AddSyncedRowAsync(account.Id, 100m, new DateOnly(2026, 8, 20));
        await AddSyncedRowAsync(
            account.Id, -40m, new DateOnly(2026, 8, 25), ClearedStatus.Uncleared, pending: true);

        var result = await _reconciliation.ReconcileAgainstSyncedBalanceAsync(account.Id);

        Assert.True(result.Finished);
        Assert.Equal(1, result.ReconciledCount);

        await using var db = _db.CreateDbContext();
        var pending = await db.Transactions.AsNoTracking().FirstAsync(t => t.Pending);
        Assert.Equal(ClearedStatus.Uncleared, pending.ClearedStatus);
        Assert.Null(pending.ReconciliationSessionId);
    }

    [Fact]
    public async Task ReconcileAgainstSyncedBalance_RowAfterBalanceDate_IsOutsideTheStatement()
    {
        // A row dated after the bank measured its balance can't be in that balance, so the
        // statement must close on BalanceAsOf and leave it for next time.
        var account = await SeedSyncedAccountAsync(openingBalance: 1000m, syncedBalance: 1100m);
        await AddSyncedRowAsync(account.Id, 100m, new DateOnly(2026, 8, 20));
        await AddSyncedRowAsync(account.Id, 500m, new DateOnly(2026, 8, 28));

        var result = await _reconciliation.ReconcileAgainstSyncedBalanceAsync(account.Id);

        Assert.True(result.Finished);
        Assert.Equal(1, result.ReconciledCount);

        await using var db = _db.CreateDbContext();
        var later = await db.Transactions.AsNoTracking()
            .FirstAsync(t => t.PostedDate == new DateOnly(2026, 8, 28));
        Assert.Equal(ClearedStatus.Cleared, later.ClearedStatus);
    }

    [Fact]
    public async Task ReconcileAgainstSyncedBalance_UsesPriorSessionsEndingBalanceAsTheStart()
    {
        var account = await SeedSyncedAccountAsync(openingBalance: 1000m, syncedBalance: 1250m);
        await InsertSessionDirectAsync(
            account.Id, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), 1000m, 1200m, isComplete: true);
        await AddSyncedRowAsync(account.Id, 50m, new DateOnly(2026, 8, 10));

        var result = await _reconciliation.ReconcileAgainstSyncedBalanceAsync(account.Id);

        Assert.True(result.Finished);
        var session = await ReloadSessionAsync(result.Session.Id);
        Assert.Equal(1200m, session.StatementStartingBalance);
        Assert.Equal(new DateOnly(2026, 8, 1), session.StatementStartDate);
    }

    [Fact]
    public async Task ReconcileAgainstSyncedBalance_AlreadyReconciledPastTheBalanceDate_Throws()
    {
        var account = await SeedSyncedAccountAsync(
            openingBalance: 1000m, syncedBalance: 1000m, balanceAsOf: new DateTime(2026, 8, 5, 9, 0, 0));
        await InsertSessionDirectAsync(
            account.Id, new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 31), 1000m, 1000m, isComplete: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _reconciliation.ReconcileAgainstSyncedBalanceAsync(account.Id));
        Assert.Contains("Sync again", ex.Message);

        Assert.Null(await _reconciliation.GetOpenSessionAsync(account.Id));
    }

    [Fact]
    public async Task ReconcileAgainstSyncedBalance_FutureBalanceDate_Throws()
    {
        var account = await SeedSyncedAccountAsync(
            openingBalance: 1000m, syncedBalance: 1000m, balanceAsOf: DateTime.Today.AddDays(1));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _reconciliation.ReconcileAgainstSyncedBalanceAsync(account.Id));
        Assert.Contains("in the future", ex.Message);
    }

    [Fact]
    public async Task ReconcileAgainstSyncedBalance_NotSynced_Throws()
    {
        var account = await SeedSyncedAccountAsync(isSynced: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _reconciliation.ReconcileAgainstSyncedBalanceAsync(account.Id));
        Assert.Contains("isn't synced", ex.Message);
    }

    [Fact]
    public async Task ReconcileAgainstSyncedBalance_BalanceOnlyAccount_Throws()
    {
        var account = await SeedSyncedAccountAsync(syncTransactionsOverride: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _reconciliation.ReconcileAgainstSyncedBalanceAsync(account.Id));
        Assert.Contains("balance only", ex.Message);
    }

    [Fact]
    public async Task ReconcileAgainstSyncedBalance_NeverSynced_Throws()
    {
        var account = await SeedSyncedAccountAsync(hasSynced: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _reconciliation.ReconcileAgainstSyncedBalanceAsync(account.Id));
        Assert.Contains("never synced", ex.Message);
    }

    [Fact]
    public async Task ReconcileAgainstSyncedBalance_OpenSessionAlreadyExists_Throws()
    {
        var account = await SeedSyncedAccountAsync(openingBalance: 1000m, syncedBalance: 1000m);
        await _reconciliation.StartSessionAsync(
            account.Id, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 15), 1000m, 1000m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _reconciliation.ReconcileAgainstSyncedBalanceAsync(account.Id));
        Assert.Contains("already has a reconciliation in progress", ex.Message);
    }
}
