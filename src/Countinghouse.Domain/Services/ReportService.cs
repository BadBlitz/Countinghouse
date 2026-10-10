using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;
using Countinghouse.Domain.Entities;

namespace Countinghouse.Domain.Services;

// One category's share of a period. Total keeps the register's sign convention (negative = money
// out) so a category that took in more than it spent — a refunded expense — reads as such instead
// of silently flipping into the spending column.
public sealed record CategorySpending(
    int? CategoryId, string CategoryName, decimal Total, int TransactionCount)
{
    public decimal Outflow => Total < 0m ? -Total : 0m;
}

public sealed record SpendingReport(
    DateOnly From,
    DateOnly To,
    List<CategorySpending> Spending,
    List<CategorySpending> Income,
    int TransactionCount)
{
    public decimal TotalSpent => Spending.Sum(row => -row.Total);
    public decimal TotalIncome => Income.Sum(row => row.Total);
    public decimal Net => TotalIncome - TotalSpent;
    public bool IsEmpty => TransactionCount == 0;
}

// One month on the net-worth chart. Each account contributes the last snapshot it has on or
// before MonthEnd; Assets and Liabilities split that by sign, so Liabilities is negative.
public sealed record NetWorthMonth(
    DateOnly MonthEnd,
    decimal Assets,
    decimal Liabilities,
    int AccountCount,
    int CarriedForwardCount)
{
    public decimal NetWorth => Assets + Liabilities;
}

public sealed record NetWorthReport(List<NetWorthMonth> Months)
{
    public bool IsEmpty => Months.Count == 0;
}

public class ReportService(IDbContextFactory<CountinghouseContext> factory)
{
    // Rows with no category are reported under their own name rather than dropped. After a QIF
    // import of 20 years of history there are plenty of them, and hiding them would understate
    // every total on the page while looking complete.
    public const string UncategorizedName = "(uncategorized)";

    public async Task<SpendingReport> GetSpendingByCategoryAsync(
        DateOnly from,
        DateOnly to,
        IReadOnlyCollection<int>? accountIds = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var query = db.Transactions.AsNoTracking()
            .Where(t => t.PostedDate >= from && t.PostedDate <= to);

        if (accountIds is { Count: > 0 })
            query = query.Where(t => accountIds.Contains(t.AccountId));

        // Transfers are internal movement, not spending. Without this a credit card payment counts
        // twice — once leaving checking, once arriving at the card — and the "Balance Adjustment"
        // category, which is Transfer for exactly this reason, would show up as a spending line.
        query = query.Where(t => t.Category == null || t.Category.CategoryType != CategoryType.Transfer);

        // Amounts are summed in memory: SQLite stores decimal as TEXT and can't aggregate it
        // server-side. Same limitation BalanceCalculator works around.
        var rows = await query
            .Select(t => new { t.CategoryId, t.Amount })
            .ToListAsync(cancellationToken);

        var names = await db.Categories.AsNoTracking()
            .Include(c => c.ParentCategory)
            .ToDictionaryAsync(c => c.Id, c => c.FullName, cancellationToken);

        var grouped = rows
            .GroupBy(t => t.CategoryId)
            .Select(g => new CategorySpending(
                g.Key,
                g.Key is null
                    ? UncategorizedName
                    : names.TryGetValue(g.Key.Value, out var name) ? name : UncategorizedName,
                g.Sum(t => t.Amount),
                g.Count()))
            .ToList();

        return new SpendingReport(
            from,
            to,
            grouped.Where(r => r.Total < 0m).OrderBy(r => r.Total).ToList(),
            grouped.Where(r => r.Total >= 0m).OrderByDescending(r => r.Total).ToList(),
            rows.Count);
    }

    // When an account has no snapshot on or before a month's end, a snapshot this many days into
    // the next month still stands in for it. That covers an account whose first snapshot landed
    // just after a month closed. Anything later would carry the next month's activity backwards.
    public const int LateSnapshotGraceDays = 5;

    // Net worth at each month end, from the first month with any snapshot up to the current one,
    // which is reported as of today.
    //
    // Snapshots are written only when something touches a balance (a sync, an edit, startup), so
    // most days have none. A month needs one reading per account, not a daily series: each account
    // contributes its last snapshot on or before the month's end, even if that snapshot is older
    // than the month itself. Those carried-forward readings are counted so the page can say so.
    public async Task<NetWorthReport> GetNetWorthByMonthAsync(
        DateOnly? today = null, CancellationToken cancellationToken = default)
    {
        var asOf = today ?? DateOnly.FromDateTime(DateTime.Today);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var snapshots = await db.AccountBalanceSnapshots.AsNoTracking()
            .Where(s => s.AsOfDate <= asOf)
            .Select(s => new { s.AccountId, s.AsOfDate, s.Balance })
            .ToListAsync(cancellationToken);

        if (snapshots.Count == 0)
            return new NetWorthReport([]);

        var closedIds = (await db.Accounts.AsNoTracking()
            .Where(a => a.IsClosed)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken)).ToHashSet();

        var byAccount = snapshots
            .GroupBy(s => s.AccountId)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.AsOfDate).ToList());

        var first = snapshots.Min(s => s.AsOfDate);
        var months = new List<NetWorthMonth>();

        for (var monthStart = new DateOnly(first.Year, first.Month, 1);
             monthStart <= asOf;
             monthStart = monthStart.AddMonths(1))
        {
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);
            var cutoff = monthEnd < asOf ? monthEnd : asOf;

            decimal assets = 0m, liabilities = 0m;
            int count = 0, carried = 0;

            foreach (var (accountId, rows) in byAccount)
            {
                var reading = rows.LastOrDefault(s => s.AsOfDate <= cutoff);

                // A closed account stops being written the day it closes, so its last snapshot is
                // its closing balance. It counts up to the month of that snapshot and not after;
                // carrying it forward would keep a closed account in net worth forever, and the
                // dashboard already leaves closed accounts out.
                if (reading is not null && closedIds.Contains(accountId)
                    && rows[^1].AsOfDate < monthStart)
                    continue;

                reading ??= rows.FirstOrDefault(s =>
                    s.AsOfDate > cutoff && s.AsOfDate <= monthEnd.AddDays(LateSnapshotGraceDays)
                    && s.AsOfDate <= asOf);

                if (reading is null)
                    continue;

                if (reading.Balance >= 0m) assets += reading.Balance;
                else liabilities += reading.Balance;
                count++;
                if (reading.AsOfDate < monthStart)
                    carried++;
            }

            months.Add(new NetWorthMonth(cutoff, assets, liabilities, count, carried));
        }

        return new NetWorthReport(months);
    }
}
