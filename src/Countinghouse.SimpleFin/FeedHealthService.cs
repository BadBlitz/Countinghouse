using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;
using Countinghouse.Domain.Entities;

namespace Countinghouse.SimpleFin;

public enum FeedHealthStatus
{
    // The feed is answering, and the balance is doing what this account's usage says it should.
    Ok,

    // The feed is answering but something about the answer is wrong. Kind says which.
    Stale,

    // The connection itself is reporting a failure. Nothing else here is worth saying: a failed
    // sync explains any staleness, and the error message is the thing to act on.
    SyncFailing,

    // There isn't enough observed history to have an opinion yet. Distinct from Ok on purpose —
    // "we can't tell" and "it's fine" are different answers and only one of them is reassuring.
    Unknown,

    // The question doesn't apply. Note says why.
    NotApplicable,
}

public enum FeedStaleKind
{
    // The bank's own timestamp is not advancing even though we keep asking and the sync keeps
    // reporting success. This is the failure that hides every other one: with the balance frozen,
    // the balance check reads the difference as an ordinary register discrepancy.
    BalanceNotAdvancing,

    // The timestamp advances but the balance never moves, past the point where this account's
    // usage says it must have. Catches the feed that keeps answering with a stale figure — the
    // case neither the balance check nor the timestamp can see.
    NoMovement,
}

public sealed record AccountFeedHealth
{
    public required int AccountId { get; init; }
    public required string AccountName { get; init; }
    public required FeedHealthStatus Status { get; init; }
    public FeedStaleKind? Kind { get; init; }

    // BalanceNotAdvancing: whole days between the bank's timestamp and our last sync.
    public int DaysBehind { get; init; }

    // NoMovement: when the current balance first appeared, and how long we have watched it sit
    // there. The span is measured to the last time we actually looked, never to today — an
    // account we haven't asked about in a month hasn't "not moved for a month".
    public DateOnly? UnchangedSince { get; init; }
    public int DaysWithoutMovement { get; init; }

    public string? Note { get; init; }

    public bool Applies => Status != FeedHealthStatus.NotApplicable;
    public bool NeedsAttention => Status is FeedHealthStatus.Stale or FeedHealthStatus.SyncFailing;
}

// Asks whether a feed is actually alive, which the balance check deliberately cannot answer.
//
// BalanceCheckService compares the register against whatever the feed last said and has no opinion
// about *when* it said it or whether the figure has moved since. So a frozen feed reads there as
// an ordinary difference, and a feed serving a stale balance on a quiet account reads as nothing
// at all. The signals below are complementary rather than overlapping:
//
//   - Feed dead but balance still moving        -> the balance check
//   - Balance frozen while syncs report success  -> BalanceNotAdvancing
//   - A quiet account's feed dies unnoticed      -> NoMovement
//
// Read-only, like the balance check. Nothing here writes and nothing auto-reconciles.
public class FeedHealthService(IDbContextFactory<CountinghouseContext> factory)
{
    // How far the bank's timestamp may sit behind our last sync before it counts as frozen.
    //
    // A one-day lag is routine and rotates between institutions; occasionally it reaches two.
    // Nothing benign has reached three, while a real credential failure runs noticeably longer.
    // Three sits one clear day above the benign range and comfortably below an actual failure.
    //
    // It is not a wide margin, and a weekday bank holiday can push a legitimate lag past it. That
    // is the accepted trade: this indicator is worth having noisy and is worthless quiet.
    public const int MaxBalanceAgeDays = 3;

    // Trading days a MarketDays account may sit unchanged before it counts as stale. A market
    // holiday accounts for one; three is a genuine signal.
    public const int MarketDayGrace = 3;

    public async Task<List<AccountFeedHealth>> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => !a.IsClosed)
            .OrderBy(a => a.Name)
            .ToListAsync(cancellationToken);
        return await CheckAsync(db, accounts, Today, cancellationToken);
    }

    public async Task<AccountFeedHealth?> CheckAccountAsync(
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

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private static string? NotApplicableReason(Account account)
    {
        if (!account.IsSynced)
            return "Not synced — there is no feed to be stale.";
        if (account.BalanceAsOf is null)
            return "Has never synced, so there is nothing to compare against yet.";
        return null;
    }

    // `today` is a parameter rather than read from the clock, matching BalanceCheckService and
    // SyncCutoffCalculator: a test that can't fix today can't exercise a boundary measured in days.
    internal static async Task<List<AccountFeedHealth>> CheckAsync(
        CountinghouseContext db,
        List<Account> accounts,
        DateOnly today,
        CancellationToken cancellationToken = default)
    {
        var checkable = accounts.Where(a => NotApplicableReason(a) is null).Select(a => a.Id).ToList();

        var connections = await db.SimpleFinConnections.AsNoTracking()
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        // Only the accounts that could have a movement expectation, and only the columns the walk
        // back through history needs. Balances are compared in memory for the usual reason: EF
        // stores decimal as TEXT in SQLite, so the comparison has to happen here anyway.
        var expectant = accounts
            .Where(a => checkable.Contains(a.Id) && a.HasMovementExpectation)
            .Select(a => a.Id)
            .ToList();

        var snapshots = expectant.Count == 0
            ? []
            : await db.AccountBalanceSnapshots.AsNoTracking()
                .Where(s => expectant.Contains(s.AccountId))
                .Select(s => new { s.AccountId, s.AsOfDate, s.Balance })
                .ToListAsync(cancellationToken);

        var historyByAccount = snapshots
            .GroupBy(s => s.AccountId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(s => s.AsOfDate)
                      .Select(s => (Date: s.AsOfDate, Balance: s.Balance))
                      .ToList());

        return accounts.Select(account =>
        {
            if (NotApplicableReason(account) is { } reason)
            {
                return new AccountFeedHealth
                {
                    AccountId = account.Id,
                    AccountName = account.Name,
                    Status = FeedHealthStatus.NotApplicable,
                    Note = reason,
                };
            }

            var connection = account.SimpleFinConnectionId is { } id
                ? connections.GetValueOrDefault(id)
                : null;

            return Evaluate(
                account,
                connection,
                historyByAccount.GetValueOrDefault(account.Id) ?? [],
                today);
        }).ToList();
    }

    private static AccountFeedHealth Evaluate(
        Account account,
        SimpleFinConnection? connection,
        List<(DateOnly Date, decimal Balance)> history,
        DateOnly today)
    {
        var health = new AccountFeedHealth
        {
            AccountId = account.Id,
            AccountName = account.Name,
            Status = FeedHealthStatus.Ok,
        };

        // A failed sync explains any staleness that follows it, and the connection's own error
        // message is more specific than anything this service could infer. Say that and stop —
        // stacking a derived complaint on top of a reported one buries the reported one.
        //
        // A partial sync is the exception: the connection answered, and only the accounts the
        // error was pinned on are failing. The rest fall through and are judged on their own.
        if (connection is { LastSyncStatus: "PartialSuccess" })
        {
            if (account.LastSyncError is { } error)
            {
                return health with
                {
                    Status = FeedHealthStatus.SyncFailing,
                    Note = $"The last sync reported a problem with this account's institution. {error}",
                };
            }
        }
        else if (connection is { LastSyncStatus: not null and not "Success" })
        {
            return health with
            {
                Status = FeedHealthStatus.SyncFailing,
                Note = $"The last sync reported {connection.LastSyncStatus}. "
                       + (connection.LastSyncErrorMessage ?? "Re-link the institution and sync again."),
            };
        }

        // We only know the feed is answering as far as the last time we asked it. Everything below
        // is measured against that instant, never against today.
        var lastAsked = connection?.LastSyncAt is { } asked ? DateOnly.FromDateTime(asked) : (DateOnly?)null;
        if (lastAsked is null)
        {
            return health with
            {
                Status = FeedHealthStatus.Unknown,
                Note = "This connection has no recorded sync, so there is nothing to measure against.",
            };
        }

        var observedThrough = lastAsked.Value < today ? lastAsked.Value : today;
        var balanceDate = DateOnly.FromDateTime(account.BalanceAsOf!.Value);
        var daysBehind = observedThrough.DayNumber - balanceDate.DayNumber;

        if (daysBehind > MaxBalanceAgeDays)
        {
            return health with
            {
                Status = FeedHealthStatus.Stale,
                Kind = FeedStaleKind.BalanceNotAdvancing,
                DaysBehind = daysBehind,
                Note = $"The bank's balance is dated {balanceDate:M/d/yyyy} but the last sync ran "
                       + $"{observedThrough:M/d/yyyy} and reported success. A feed that keeps "
                       + "answering with the same timestamp is usually a broken credential the API "
                       + "does not report — re-authenticate the institution and sync again.",
            };
        }

        if (!account.HasMovementExpectation)
            return health;

        return EvaluateMovement(account, health, history, observedThrough);
    }

    private static AccountFeedHealth EvaluateMovement(
        Account account,
        AccountFeedHealth health,
        List<(DateOnly Date, decimal Balance)> history,
        DateOnly observedThrough)
    {
        var marketDays = account.ExpectedMovement == MovementExpectation.MarketDays;

        // Weekend observations of a market account are not evidence: a feed can re-serve the prior
        // weekday's balance verbatim over the weekend while other accounts on the same sync update
        // normally. Comparing across weekend observations would report no movement every weekend.
        var usable = marketDays
            ? history.Where(h => IsWeekday(h.Date)).ToList()
            : history;

        if (usable.Count < 2)
        {
            return health with
            {
                Status = FeedHealthStatus.Unknown,
                Note = marketDays
                    ? "Not enough weekday balances recorded yet to tell whether this account is "
                      + "moving. Syncing on a weekday will start the series."
                    : "Not enough balance history recorded yet to tell whether this account is moving.",
            };
        }

        // Walk back through the trailing run of observations that still hold the current balance.
        // The oldest of them is when this figure first appeared.
        var current = usable[0].Balance;
        var unchangedSince = usable[0].Date;
        var sawAChange = false;
        foreach (var (date, balance) in usable)
        {
            if (balance != current)
            {
                sawAChange = true;
                break;
            }
            unchangedSince = date;
        }

        var span = marketDays
            ? WeekdaysBetween(unchangedSince, observedThrough)
            : observedThrough.DayNumber - unchangedSince.DayNumber;
        var limit = marketDays ? MarketDayGrace : account.ExpectedMovementDays!.Value;

        if (span <= limit)
            return health with { UnchangedSince = unchangedSince, DaysWithoutMovement = span };

        // A series that has only ever held one value is a weaker statement than one that moved and
        // then stopped: the account may simply have been dormant before we started watching. Still
        // worth reporting — it is also the shape a feed that was never alive takes.
        var caveat = sawAChange
            ? ""
            : " Every balance recorded for this account has been the same, so this may predate the "
              + "snapshot series rather than be a new failure.";

        return health with
        {
            Status = FeedHealthStatus.Stale,
            Kind = FeedStaleKind.NoMovement,
            UnchangedSince = unchangedSince,
            DaysWithoutMovement = span,
            Note = marketDays
                ? $"Unchanged since {unchangedSince:M/d/yyyy}, across {span} trading days. An "
                  + $"account tracking market value should move on every one of them.{caveat}"
                : $"Unchanged since {unchangedSince:M/d/yyyy}, {span} days, and this account is "
                  + $"expected to move at least every {limit}.{caveat}",
        };
    }

    private static bool IsWeekday(DateOnly date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    // Trading days approximated as weekdays. Market holidays are not modelled — MarketDayGrace
    // absorbs them, which is cheaper than carrying a holiday calendar that has to be maintained
    // forever to keep one indicator from crying wolf once a quarter.
    internal static int WeekdaysBetween(DateOnly from, DateOnly to)
    {
        if (to <= from)
            return 0;
        var count = 0;
        for (var d = from.AddDays(1); d <= to; d = d.AddDays(1))
        {
            if (IsWeekday(d))
                count++;
        }
        return count;
    }
}
