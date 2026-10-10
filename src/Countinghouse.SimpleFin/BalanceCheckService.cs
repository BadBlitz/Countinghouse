using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;
using Countinghouse.Domain.Entities;

namespace Countinghouse.SimpleFin;

public enum BalanceCheckStatus
{
    // The register accounts for the synced balance exactly.
    Balanced,

    // It doesn't. Difference says by how much, and Note names the likeliest reason.
    Off,

    // The question doesn't apply to this account. Note says why.
    NotApplicable,
}

public sealed record AccountBalanceCheck
{
    public required int AccountId { get; init; }
    public required string AccountName { get; init; }
    public required BalanceCheckStatus Status { get; init; }

    // SyncedBalance - RegisterBalance. Positive means the register is short: money the bank
    // counts that we can't account for, which is what a silently-dropped transaction looks like.
    public decimal Difference { get; init; }

    // OpeningBalance + every posted row. Not Account.CurrentBalance — for a synced account that
    // figure comes straight from the API and is exactly what this is being compared against.
    public decimal RegisterBalance { get; init; }
    public decimal SyncedBalance { get; init; }

    // Pending rows are excluded from RegisterBalance deliberately (see the service). Surfaced so
    // that a register whose visible running total disagrees with the figure here isn't a mystery.
    public int PendingCount { get; init; }

    // Why the check doesn't apply, or the likeliest reason it's off. Null when there's nothing
    // useful to add — an unexplained difference is the case that deserves a look.
    public string? Note { get; init; }

    public bool Applies => Status != BalanceCheckStatus.NotApplicable;
}

// Answers one question per account: does the register add up to the balance the bank reports?
//
// A too-narrow start-date lookback drops transactions *silently* — the balance stays right,
// because it comes straight from the API and BalanceCalculator no-ops for synced accounts, so the
// only symptom is a register that no longer sums to the displayed figure. This is the standing
// detector for that failure.
//
// Deliberately read-only and manually triggered. Nothing here writes, and in particular nothing
// here marks a row Reconciled: that's a one-way door the sync upsert refuses to walk back, so
// auto-marking on a match would progressively lock the register, and a match produced by two
// offsetting errors would lock in bad data and destroy the evidence. Automatic arithmetic,
// manual consequences.
public class BalanceCheckService(IDbContextFactory<CountinghouseContext> factory)
{
    public async Task<List<AccountBalanceCheck>> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => !a.IsClosed)
            .OrderBy(a => a.Name)
            .ToListAsync(cancellationToken);
        return await CheckAsync(db, accounts, Today, cancellationToken);
    }

    public async Task<AccountBalanceCheck?> CheckAccountAsync(
        int accountId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
            return null;
        var results = await CheckAsync(db, [account], Today, cancellationToken);
        return results[0];
    }

    private static DateOnly Today => SyncCutoffCalculator.ServerToday(DateTimeOffset.UtcNow);

    // An account has to be synced to have an independent figure to check against; has to keep a
    // register for there to be anything to add up; and has to have synced at least once for the
    // balance to mean anything.
    private static string? NotApplicableReason(Account account)
    {
        if (!account.IsSynced)
            return "Not synced — its balance is derived from the register, so this would always agree.";
        if (!account.SyncsTransactions)
            return "Tracks a balance only, so there is no register to add up.";
        if (account.BalanceAsOf is null)
            return "Has never synced, so there is no bank balance to check against.";
        return null;
    }

    // `today` is a parameter rather than read from the clock, exactly as SyncCutoffCalculator
    // takes it — the gap explanation is measured against it, and a test that can't fix it can't
    // exercise the boundary it's describing.
    internal static async Task<List<AccountBalanceCheck>> CheckAsync(
        CountinghouseContext db,
        List<Account> accounts,
        DateOnly today,
        CancellationToken cancellationToken = default)
    {
        var checkable = accounts.Where(a => NotApplicableReason(a) is null).Select(a => a.Id).ToList();

        // Summed in memory, as everywhere else that touches money: SQLite stores decimal as TEXT
        // and can't aggregate it server-side. Pending is a bool, so that filter does run in SQL
        // and keeps the payload to the rows that actually count.
        var postedAmounts = await db.Transactions.AsNoTracking()
            .Where(t => checkable.Contains(t.AccountId) && !t.Pending)
            .Select(t => new { t.AccountId, t.Amount })
            .ToListAsync(cancellationToken);

        var pendingCounts = await db.Transactions.AsNoTracking()
            .Where(t => checkable.Contains(t.AccountId) && t.Pending)
            .GroupBy(t => t.AccountId)
            .Select(g => new { AccountId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var lastKnownDates = await db.Transactions.AsNoTracking()
            .Where(t => checkable.Contains(t.AccountId))
            .GroupBy(t => t.AccountId)
            .Select(g => new { AccountId = g.Key, Last = g.Max(t => t.PostedDate) })
            .ToListAsync(cancellationToken);

        var postedByAccount = postedAmounts
            .GroupBy(t => t.AccountId)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));
        var pendingByAccount = pendingCounts.ToDictionary(p => p.AccountId, p => p.Count);
        var lastKnownByAccount = lastKnownDates.ToDictionary(d => d.AccountId, d => d.Last);

        return accounts.Select(account =>
        {
            if (NotApplicableReason(account) is { } reason)
            {
                return new AccountBalanceCheck
                {
                    AccountId = account.Id,
                    AccountName = account.Name,
                    Status = BalanceCheckStatus.NotApplicable,
                    SyncedBalance = account.CurrentBalance,
                    Note = reason,
                };
            }

            var posted = postedByAccount.GetValueOrDefault(account.Id);
            var registerBalance = account.OpeningBalance + posted;
            var difference = account.CurrentBalance - registerBalance;
            var lastKnown = lastKnownByAccount.TryGetValue(account.Id, out var known)
                ? (DateOnly?)known
                : null;

            return new AccountBalanceCheck
            {
                AccountId = account.Id,
                AccountName = account.Name,
                Status = difference == 0m ? BalanceCheckStatus.Balanced : BalanceCheckStatus.Off,
                Difference = difference,
                RegisterBalance = registerBalance,
                SyncedBalance = account.CurrentBalance,
                PendingCount = pendingByAccount.GetValueOrDefault(account.Id),
                Note = difference == 0m ? null : ExplainDifference(account, lastKnown, today, difference),
            };
        }).ToList();
    }

    // Only ever consulted for an account that didn't balance. A known gap is reported as the
    // likely cause rather than used to suppress the check: if such an account balances anyway,
    // that is proof nothing was dropped, and discarding that proof to avoid displaying a
    // difference we already know how to explain would be the worse trade.
    private static string? ExplainDifference(
        Account account, DateOnly? lastKnown, DateOnly today, decimal difference)
    {
        if (lastKnown is null)
            return "The register is empty, so there is nothing to add up yet.";

        var cutoff = new AccountCutoff(
            account.Id, account.SimpleFinAccountId, null, lastKnown, account.SyncsTransactions);

        if (cutoff.UnreachableGap(today) is not { } gap)
        {
            // A register holding *more* than the bank reports can't be a dropped inflow, and the
            // likeliest cause isn't a missing row at all: some banks publish a balance that is
            // posted minus scheduled outgoing payments, so money already deducted for a payment
            // not yet published as a transaction shows up exactly like this. Point at the
            // account's scheduled/pending payments before sending anyone hunting for a lost row.
            if (difference < 0m)
                return "The register holds more than the bank reports. Some banks subtract scheduled "
                       + "or pending payments from the balance they publish without listing them as "
                       + "transactions — check the account's scheduled payments before treating this "
                       + "as a missing transaction.";
            return null;
        }

        var (from, to) = gap;
        return $"History stops at {lastKnown:M/d/yyyy}, before the "
               + $"{SyncCutoffCalculator.MaxLookbackDays}-day sync window opens. Anything dated "
               + $"{from:M/d/yyyy}–{to:M/d/yyyy} is out of SimpleFIN's reach and can only be "
               + "restored from a fresh QIF export.";
    }
}
