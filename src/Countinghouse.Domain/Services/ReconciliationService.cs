using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;
using Countinghouse.Domain.Entities;

namespace Countinghouse.Domain.Services;

// What to pre-fill the setup form with. StartingBalance stays editable in the UI because the first
// real statement rarely begins exactly at the QIF cutoff; ReconciliationWorksheet.StartingBalanceDrift
// is what stops a nudged figure from quietly hiding an earlier statement's problem.
public sealed record ReconciliationSuggestion(
    decimal StartingBalance,
    DateOnly StartDate,
    DateOnly EndDate,
    bool IsFirstSession);

public sealed record ReconciliationWorksheet(
    ReconciliationSession Session,
    Account Account,
    List<Transaction> Rows,
    decimal ClearedBalance,
    decimal Difference,
    int ClearedCount,
    // OpeningBalance + every already-Reconciled row. If this disagrees with the statement's
    // starting balance, a previously-reconciled row has been edited or deleted since — the
    // difference will never reach zero and it isn't this statement's fault.
    decimal ReconciledThroughBalance)
{
    public bool IsBalanced => Difference == 0m;
    public decimal StartingBalanceDrift => Session.StatementStartingBalance - ReconciledThroughBalance;
}

public class ReconciliationService(IDbContextFactory<CountinghouseContext> factory)
{
    public async Task<ReconciliationSession?> GetOpenSessionAsync(int accountId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.ReconciliationSessions.AsNoTracking()
            .Where(s => s.AccountId == accountId && !s.IsComplete)
            .OrderByDescending(s => s.Id)
            .FirstOrDefaultAsync();
    }

    public async Task<List<ReconciliationSession>> GetHistoryAsync(int accountId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.ReconciliationSessions.AsNoTracking()
            .Where(s => s.AccountId == accountId && s.IsComplete)
            .OrderByDescending(s => s.StatementEndDate).ThenByDescending(s => s.Id)
            .ToListAsync();
    }

    public async Task<ReconciliationSuggestion> GetSuggestionAsync(int accountId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var account = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == accountId);
        var previous = await db.ReconciliationSessions.AsNoTracking()
            .Where(s => s.AccountId == accountId && s.IsComplete)
            .OrderByDescending(s => s.StatementEndDate).ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync();

        if (previous is not null)
        {
            var start = previous.StatementEndDate.AddDays(1);
            return new ReconciliationSuggestion(
                previous.StatementEndingBalance, start, SuggestedEnd(start), false);
        }

        // First session: the statement picks up where the imported history left off, so the
        // starting balance is everything already reconciled, not the raw opening balance.
        var startingBalance = await ReconciledThroughBalanceAsync(db, account);
        var oldestUnreconciled = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId == accountId && t.ClearedStatus != ClearedStatus.Reconciled)
            .OrderBy(t => t.PostedDate).ThenBy(t => t.Id)
            .FirstOrDefaultAsync();
        var firstStart = oldestUnreconciled?.PostedDate
            ?? account.OpeningBalanceDate
            ?? DateOnly.FromDateTime(DateTime.Today);

        return new ReconciliationSuggestion(
            startingBalance, firstStart, SuggestedEnd(firstStart), true);
    }

    // A month-long window from the first unreconciled row routinely lands in the future, and a
    // statement can only cover a period that has already closed — StartSessionAsync refuses a
    // future end date, so never propose one either. Floored at the start date to keep the
    // suggestion internally consistent; a start date that is itself in the future still gets
    // refused at Start, which is the right answer rather than something to paper over here.
    private static DateOnly SuggestedEnd(DateOnly start)
    {
        var natural = start.AddMonths(1).AddDays(-1);
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (natural <= today)
            return natural;
        return today >= start ? today : start;
    }

    public async Task<ReconciliationSession> StartSessionAsync(
        int accountId,
        DateOnly startDate,
        DateOnly endDate,
        decimal startingBalance,
        decimal endingBalance)
    {
        if (endDate < startDate)
            throw new InvalidOperationException("The statement end date is before its start date.");

        // Closing a statement whose period hasn't happened yet would sweep every transaction that
        // arrives before that date into it, locking them to Reconciled against a statement that
        // doesn't list them — and they can never be ticked against the one that does.
        if (endDate > DateOnly.FromDateTime(DateTime.Today))
            throw new InvalidOperationException(
                "The statement end date is in the future. Reconcile against a period that has already closed.");

        await using var db = await factory.CreateDbContextAsync();
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId);

        // Balance-only accounts have no register to reconcile against: their transactions are
        // discarded on arrival and the balance comes straight from the API. See Account.SyncsTransactions.
        if (!account.SyncsTransactions)
            throw new InvalidOperationException(
                $"{account.Name} tracks a balance only, so there is no register to reconcile.");

        if (await db.ReconciliationSessions.AnyAsync(s => s.AccountId == accountId && !s.IsComplete))
            throw new InvalidOperationException(
                $"{account.Name} already has a reconciliation in progress.");

        var session = new ReconciliationSession
        {
            AccountId = accountId,
            StatementStartDate = startDate,
            StatementEndDate = endDate,
            StatementStartingBalance = startingBalance,
            StatementEndingBalance = endingBalance,
        };
        db.ReconciliationSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    public async Task<ReconciliationWorksheet?> GetWorksheetAsync(int sessionId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var session = await db.ReconciliationSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session is null)
            return null;

        var account = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == session.AccountId);

        // Candidates are everything not already reconciled and dated on or before the statement
        // closes. An uncleared row older than the statement start still belongs here — that's an
        // outstanding check, and dropping it would be the whole point of reconciling.
        var rows = await db.Transactions.AsNoTracking()
            .Include(t => t.Category).ThenInclude(c => c!.ParentCategory)
            .Where(t => t.AccountId == session.AccountId
                        && t.ClearedStatus != ClearedStatus.Reconciled
                        && t.PostedDate <= session.StatementEndDate)
            .OrderBy(t => t.PostedDate).ThenBy(t => t.Id)
            .ToListAsync();

        var cleared = rows.Where(t => t.ClearedStatus == ClearedStatus.Cleared).ToList();
        var clearedBalance = session.StatementStartingBalance + cleared.Sum(t => t.Amount);

        return new ReconciliationWorksheet(
            session,
            account,
            rows,
            clearedBalance,
            session.StatementEndingBalance - clearedBalance,
            cleared.Count,
            await ReconciledThroughBalanceAsync(db, account));
    }

    // The checkbox in the worksheet. Reconciled rows belong to a closed statement and are never
    // reachable from here — reopening one would have to be a deliberate feature, not an
    // accidental click.
    public async Task SetClearedAsync(int transactionId, bool cleared)
    {
        await using var db = await factory.CreateDbContextAsync();
        var txn = await db.Transactions.FirstOrDefaultAsync(t => t.Id == transactionId);
        if (txn is null || txn.ClearedStatus == ClearedStatus.Reconciled)
            return;
        txn.ClearedStatus = cleared ? ClearedStatus.Cleared : ClearedStatus.Uncleared;
        await db.SaveChangesAsync();
    }

    public async Task SetAllClearedAsync(int sessionId, bool cleared)
    {
        await using var db = await factory.CreateDbContextAsync();
        var session = await db.ReconciliationSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session is null)
            return;

        var rows = await db.Transactions
            .Where(t => t.AccountId == session.AccountId
                        && t.ClearedStatus != ClearedStatus.Reconciled
                        && t.PostedDate <= session.StatementEndDate)
            .ToListAsync();
        foreach (var txn in rows)
            txn.ClearedStatus = cleared ? ClearedStatus.Cleared : ClearedStatus.Uncleared;
        await db.SaveChangesAsync();
    }

    // The outcome of reconciling against the synced balance. Finished is false when the session
    // was opened but didn't balance — the session is left open on purpose so the user lands in the
    // ordinary worksheet, which is precisely the case that deserves a look.
    public sealed record SyncedBalanceResult(
        ReconciliationSession Session,
        bool Finished,
        int ReconciledCount,
        decimal Difference);

    // Quicken's "reconcile against the downloaded balance". Deliberately not a second code path:
    // it starts the ordinary session with its ending balance taken from Account.CurrentBalance and
    // its end date from Account.BalanceAsOf, then finishes it only if the difference is already
    // zero. Everything StartSessionAsync and FinishAsync refuse, this refuses too.
    //
    // Two things it will never do. It never posts an adjustment — FinishAsync is called with
    // postAdjustment: false, so a session that doesn't balance stays open rather than booking the
    // difference away and destroying the evidence. And it is never fired automatically on sync:
    // Reconciled is a one-way door the sync upsert refuses to walk back, so an automatic match
    // would progressively lock the register, and a match produced by two offsetting errors would
    // lock in bad data. Automatic arithmetic, manual trigger.
    public async Task<SyncedBalanceResult> ReconcileAgainstSyncedBalanceAsync(int accountId)
    {
        Account account;
        await using (var db = await factory.CreateDbContextAsync())
            account = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == accountId);

        // The same three preconditions BalanceCheckService reports as NotApplicable, for the same
        // reasons: without a connection there is no independent figure to reconcile against, a
        // balance-only account has no register to add up, and an account that has never synced has
        // no bank balance at all.
        if (!account.IsSynced)
            throw new InvalidOperationException(
                $"{account.Name} isn't synced, so there is no downloaded balance to reconcile against.");
        if (!account.SyncsTransactions)
            throw new InvalidOperationException(
                $"{account.Name} tracks a balance only, so there is no register to reconcile.");
        if (account.BalanceAsOf is not { } asOf)
            throw new InvalidOperationException(
                $"{account.Name} has never synced, so there is no downloaded balance to reconcile against.");

        // The bank's own timestamp, exactly as BalanceSnapshotWriter dates its rows from it: the
        // balance is a fact about that day, and dating the statement from anything else would
        // reconcile the register against a figure measured on a different day.
        var endDate = DateOnly.FromDateTime(asOf);
        if (endDate > DateOnly.FromDateTime(DateTime.Today))
            throw new InvalidOperationException(
                $"The synced balance is dated {endDate:M/d/yyyy}, which is in the future. "
                + "Sync again before reconciling against it.");

        var suggestion = await GetSuggestionAsync(accountId);
        if (suggestion.StartDate > endDate)
            throw new InvalidOperationException(
                $"{account.Name} is already reconciled through {suggestion.StartDate.AddDays(-1):M/d/yyyy}, "
                + $"and the synced balance is only current to {endDate:M/d/yyyy}. Sync again first.");

        var session = await StartSessionAsync(
            accountId, suggestion.StartDate, endDate, suggestion.StartingBalance, account.CurrentBalance);

        // Nothing is ticked here. Synced rows already arrive Cleared and pending ones arrive
        // Uncleared, which is the split the bank's posted balance is drawn along; ticking the rest
        // would be asserting something about them that nobody has checked.
        var worksheet = await GetWorksheetAsync(session.Id);
        if (worksheet is null || !worksheet.IsBalanced)
            return new SyncedBalanceResult(session, false, 0, worksheet?.Difference ?? 0m);

        var result = await FinishAsync(session.Id, postAdjustment: false);
        return new SyncedBalanceResult(session, true, result.ReconciledCount, 0m);
    }

    public sealed record FinishResult(int ReconciledCount, decimal AdjustmentAmount);

    // Flips the session's cleared rows to Reconciled and closes the statement. postAdjustment
    // mirrors Quicken's "Finish Anyway": it books the leftover difference rather than pretending
    // the statement balanced, so the next session's starting figure is still trustworthy.
    public async Task<FinishResult> FinishAsync(int sessionId, bool postAdjustment = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        var session = await db.ReconciliationSessions.FirstAsync(s => s.Id == sessionId);
        if (session.IsComplete)
            throw new InvalidOperationException("That reconciliation has already been finished.");

        var cleared = await db.Transactions
            .Where(t => t.AccountId == session.AccountId
                        && t.ClearedStatus == ClearedStatus.Cleared
                        && t.PostedDate <= session.StatementEndDate)
            .ToListAsync();

        var difference = session.StatementEndingBalance
                         - (session.StatementStartingBalance + cleared.Sum(t => t.Amount));

        if (difference != 0m && !postAdjustment)
            throw new InvalidOperationException(
                $"The statement is off by {difference:C2}. Finish with an adjustment, or keep marking rows.");

        foreach (var txn in cleared)
        {
            txn.ClearedStatus = ClearedStatus.Reconciled;
            txn.ReconciliationSessionId = session.Id;
        }

        var adjustment = 0m;
        if (difference != 0m)
        {
            var category = await AccountService.GetOrCreateAdjustmentCategoryAsync(db);
            db.Transactions.Add(new Transaction
            {
                AccountId = session.AccountId,
                Amount = difference,
                PostedDate = session.StatementEndDate,
                Payee = AccountService.AdjustmentCategoryName,
                Memo = $"Reconciliation adjustment, statement ending {session.StatementEndDate:yyyy-MM-dd}",
                CategoryId = category.Id,
                ClearedStatus = ClearedStatus.Reconciled,
                Source = TransactionSource.Manual,
                ReconciliationSessionId = session.Id,
            });
            adjustment = difference;
        }

        session.IsComplete = true;
        session.CompletedAt = DateTime.Now;
        await db.SaveChangesAsync();
        await BalanceCalculator.RecalculateAsync(db, session.AccountId);

        return new FinishResult(cleared.Count, adjustment);
    }

    // Abandoning a session throws away the session row only. Cleared marks stay put: they are
    // real statements of fact about the register, and a synced row arrives Cleared anyway.
    public async Task CancelSessionAsync(int sessionId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var session = await db.ReconciliationSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session is null || session.IsComplete)
            return;
        db.ReconciliationSessions.Remove(session);
        await db.SaveChangesAsync();
    }

    private static async Task<decimal> ReconciledThroughBalanceAsync(CountinghouseContext db, Account account)
    {
        // Summed in memory: SQLite stores decimal as TEXT and can't aggregate it server-side.
        var amounts = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId == account.Id && t.ClearedStatus == ClearedStatus.Reconciled)
            .Select(t => t.Amount)
            .ToListAsync();
        return account.OpeningBalance + amounts.Sum();
    }
}
