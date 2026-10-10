using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;
using Countinghouse.Domain.Entities;

namespace Countinghouse.SimpleFin;

// The last locally-known day for one account. Transactions dated on or before this are already
// in the register from the QIF import and must not be inserted again — QIF rows carry no
// ExternalId, so nothing downstream can dedupe them.
// Two dates that are easy to conflate and must not be.
//
// LastImportedDate is the QIF boundary and answers "what would duplicate?" — it is fixed forever,
// because QIF rows carry no ExternalId and nothing can ever dedupe them.
//
// LastKnownDate is the newest row from any source and answers "what do we already have?" — it
// advances with every sync. Using the import boundary for coverage questions would mean that a
// few weeks after the import, an account synced faithfully every day still looks like it has a
// hole in it.
public sealed record AccountCutoff(
    int AccountId,
    string? SimpleFinAccountId,
    DateOnly? LastImportedDate,
    DateOnly? LastKnownDate,
    bool SyncsTransactions = true)
{
    public bool Discards(DateOnly postedDate) =>
        LastImportedDate is { } cutoff && postedDate <= cutoff;

    // The span between where this account's history actually stops and the oldest day SimpleFIN
    // will still serve. Nothing can fill it but a fresh QIF export from Quicken, so it gets named
    // rather than left as a generic "partial sync" note.
    public (DateOnly From, DateOnly To)? UnreachableGap(DateOnly today)
    {
        if (LastKnownDate is not { } known)
            return null;

        var firstMissing = known.AddDays(1);
        var oldestAvailable = SyncCutoffCalculator.EarliestReachableDate(today);
        return firstMissing < oldestAvailable ? (firstMissing, oldestAvailable.AddDays(-1)) : null;
    }
}

// GET /accounts takes ONE start-date per Access URL, but each account has its own last-imported
// date — a single connection can span several accounts with very different import histories. No
// single start-date is right: the latest would silently drop older activity on one account, the
// earliest would duplicate history on another. So the request is widened to cover the earliest
// cutoff, and each account then discards what it already has.
//
// Widening is always safe and only costs payload; narrowing loses data silently. That asymmetry
// is why every judgement call here errs toward asking for too much.
public static class SyncCutoffCalculator
{
    public static async Task<List<AccountCutoff>> ForConnectionAsync(
        CountinghouseContext db, int connectionId, CancellationToken cancellationToken = default)
    {
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.SimpleFinConnectionId == connectionId)
            .Select(a => new { a.Id, a.SimpleFinAccountId, a.AccountType, a.SyncTransactionsOverride })
            .ToListAsync(cancellationToken);

        var accountIds = accounts.Select(a => a.Id).ToList();

        // Only import-batch rows count toward the discard boundary. A hand-entered correction
        // sitting after the imported history would otherwise push the cutoff forward and hide
        // real transactions behind it.
        var importedDates = await db.Transactions.AsNoTracking()
            .Where(t => accountIds.Contains(t.AccountId) && t.ImportBatchId != null)
            .GroupBy(t => t.AccountId)
            .Select(g => new { AccountId = g.Key, Last = g.Max(t => t.PostedDate) })
            .ToListAsync(cancellationToken);

        // Coverage, by contrast, counts everything: a synced row proves we have that day.
        var knownDates = await db.Transactions.AsNoTracking()
            .Where(t => accountIds.Contains(t.AccountId))
            .GroupBy(t => t.AccountId)
            .Select(g => new { AccountId = g.Key, Last = g.Max(t => t.PostedDate) })
            .ToListAsync(cancellationToken);

        var importedByAccount = importedDates.ToDictionary(d => d.AccountId, d => d.Last);
        var knownByAccount = knownDates.ToDictionary(d => d.AccountId, d => d.Last);

        return accounts
            .Select(a => new AccountCutoff(
                a.Id,
                a.SimpleFinAccountId,
                importedByAccount.TryGetValue(a.Id, out var imported) ? imported : null,
                knownByAccount.TryGetValue(a.Id, out var known) ? known : null,
                // Mirrors Account.SyncsTransactions; computed here because EF can't translate it.
                a.SyncTransactionsOverride ?? a.AccountType != AccountType.Investment))
            .ToList();
    }

    // SimpleFIN hard-caps at 90 days and warns above 45 ("recommended range"), reporting both
    // through the `errors` list on an otherwise-successful response — so either one costs a
    // spurious "partial sync" badge on every single run, which trains you to ignore the one
    // warning that actually matters.
    //
    // 45 is a deliberate choice, not the protocol's limit: this app is synced far more often than
    // that, so a longer window buys nothing but noise. Raise it if syncs ever become infrequent —
    // anything beyond this is reported as an unreachable gap, which at 90 would stay true but at
    // 45 is a self-imposed boundary rather than the server's.
    public const int MaxLookbackDays = 45;

    // A day is given back on purpose, and removing it will silently reintroduce the warning.
    // `start-date` is a midnight timestamp but the server measures the span against the current
    // instant, so requesting midnight exactly N days ago is N days *plus* however far into today
    // it happens to be — 45.9 days for an evening sync, which trips a 45-day limit. One day less
    // is inside it no matter what time the sync runs.
    public static DateOnly EarliestReachableDate(DateOnly today) =>
        today.AddDays(-(MaxLookbackDays - 1));

    // "Today" for every window measured here is the UTC date, because the server counts back from
    // the current UTC instant. The local date lags it every evening west of Greenwich, and a window
    // counted from the local date then runs a day long: the day given back above is spent, and the
    // warning returns on every evening sync.
    public static DateOnly ServerToday(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime);

    // The protocol's hard cap, used only to seed an account holding no rows at all. That one
    // request is worth its "exceeds recommended range" warning: it is the only opportunity to
    // pick up more than six weeks of past activity for an account with no history to build on.
    public const int FirstSyncLookbackDays = 90;

    public static DateOnly FirstSyncDate(DateOnly today) =>
        today.AddDays(-(FirstSyncLookbackDays - 1));

    // The start-date to request for the whole connection. Never omitted: omitting start-date does
    // not mean "everything" — it means the Bridge's own default, which in practice returns almost
    // no transactions, so every other mapped account can come back empty on the same sync.
    //
    // Also deliberately not falling back to an account's OpeningBalanceDate when nothing has been
    // imported: snapshot accounts are stamped with today as their opening date, so that fallback
    // would compute a cutoff of today and discard every transaction the first sync returns — the
    // exact silent-drop failure this class exists to prevent. An account with no imported rows
    // gets no cutoff at all.
    public static DateOnly EarliestRequestDate(IEnumerable<AccountCutoff> cutoffs, DateOnly today)
    {
        var list = cutoffs.ToList();

        // An account holding no rows at all is on its first sync: there is no local history to
        // work forward from, so reach back as far as the protocol allows. Keyed off LastKnownDate
        // rather than the import boundary — accounts with no imported history will never have an
        // import boundary, so keying off that would request a full history on every sync forever.
        //
        // Balance-only accounts are excluded, and permanently satisfy the "no rows" test if they
        // aren't: their transactions are discarded on arrival, so letting them widen the window
        // would buy a bigger payload and a standing "exceeds recommended range" warning in
        // exchange for data that is thrown away on every single sync.
        if (list.Any(c => c.SyncsTransactions && c.LastKnownDate is null))
            return FirstSyncDate(today);

        // Otherwise: always the full lookback window, not merely the span since the newest row.
        //
        // Wider than strictly needed, deliberately. Re-fetching the last few weeks is what lets
        // the upsert catch revisions — a pending charge settling at a different amount, a payee
        // being cleaned up — which a request starting at "the day after our newest row" would
        // never see. The per-account cutoff blocks duplicates regardless of how wide this is, so
        // the only cost is payload.
        return EarliestReachableDate(today);
    }
}
