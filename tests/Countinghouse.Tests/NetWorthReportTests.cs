using Countinghouse.Domain.Entities;
using Countinghouse.Domain.Services;

namespace Countinghouse.Tests;

// The net-worth report reads snapshots directly, so these tests write accounts and snapshots
// straight into the database. Going through AccountService would record an extra snapshot dated
// today for every account it touches.
public class NetWorthReportTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ReportService _reports;

    public NetWorthReportTests() => _reports = new ReportService(_db);

    public void Dispose() => _db.Dispose();

    private static readonly DateOnly Today = new(2026, 3, 10);

    private async Task<int> AccountAsync(string name, bool isClosed = false)
    {
        await using var db = _db.CreateDbContext();
        var account = new Account { Name = name, AccountType = AccountType.Checking, IsClosed = isClosed };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task SnapshotAsync(int accountId, DateOnly date, decimal balance)
    {
        await using var db = _db.CreateDbContext();
        db.AccountBalanceSnapshots.Add(new AccountBalanceSnapshot
        {
            AccountId = accountId,
            AsOfDate = date,
            Balance = balance,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task NoSnapshots_ReportsEmpty()
    {
        await AccountAsync("Checking");

        var report = await _reports.GetNetWorthByMonthAsync(Today);

        Assert.True(report.IsEmpty);
    }

    [Fact]
    public async Task UsesLastSnapshotOnOrBeforeMonthEnd()
    {
        var checking = await AccountAsync("Checking");
        await SnapshotAsync(checking, new(2026, 1, 5), 100m);
        await SnapshotAsync(checking, new(2026, 1, 28), 250m);
        await SnapshotAsync(checking, new(2026, 2, 3), 900m);

        var report = await _reports.GetNetWorthByMonthAsync(Today);

        Assert.Equal(new DateOnly(2026, 1, 31), report.Months[0].MonthEnd);
        Assert.Equal(250m, report.Months[0].NetWorth);
    }

    // A reading from a few days into the next month is closer to the month end, but it already
    // carries the next month's activity. It only counts when nothing earlier exists.
    [Fact]
    public async Task PrefersAnEarlierSnapshotOverACloserLaterOne()
    {
        var checking = await AccountAsync("Checking");
        await SnapshotAsync(checking, new(2026, 1, 20), 100m);
        await SnapshotAsync(checking, new(2026, 2, 1), 500m);

        var report = await _reports.GetNetWorthByMonthAsync(Today);

        Assert.Equal(100m, report.Months[0].NetWorth);
    }

    [Fact]
    public async Task AccountWithNoEarlierSnapshot_UsesOneJustAfterMonthEnd()
    {
        var checking = await AccountAsync("Checking");
        var card = await AccountAsync("Card");
        await SnapshotAsync(checking, new(2026, 1, 20), 100m);
        await SnapshotAsync(card, new(2026, 2, ReportService.LateSnapshotGraceDays), -40m);

        var report = await _reports.GetNetWorthByMonthAsync(Today);

        Assert.Equal(60m, report.Months[0].NetWorth);
        Assert.Equal(2, report.Months[0].AccountCount);
    }

    [Fact]
    public async Task SnapshotBeyondTheGrace_DoesNotReachBackIntoTheMonth()
    {
        var checking = await AccountAsync("Checking");
        var card = await AccountAsync("Card");
        await SnapshotAsync(checking, new(2026, 1, 20), 100m);
        await SnapshotAsync(card, new(2026, 2, ReportService.LateSnapshotGraceDays + 1), -40m);

        var report = await _reports.GetNetWorthByMonthAsync(Today);

        Assert.Equal(100m, report.Months[0].NetWorth);
        Assert.Equal(1, report.Months[0].AccountCount);
    }

    // Most days have no snapshot. A month with none for an account still counts that account, at
    // its last known balance, and says it did so.
    [Fact]
    public async Task MonthWithoutASnapshot_CarriesTheLastOneForward()
    {
        var savings = await AccountAsync("Savings");
        await SnapshotAsync(savings, new(2026, 1, 15), 1000m);

        var report = await _reports.GetNetWorthByMonthAsync(Today);

        Assert.Equal(3, report.Months.Count);
        Assert.All(report.Months, m => Assert.Equal(1000m, m.NetWorth));
        Assert.Equal(0, report.Months[0].CarriedForwardCount);
        Assert.Equal(1, report.Months[1].CarriedForwardCount);
    }

    [Fact]
    public async Task CurrentMonth_IsReportedAsOfToday()
    {
        var checking = await AccountAsync("Checking");
        await SnapshotAsync(checking, new(2026, 3, 2), 300m);

        var report = await _reports.GetNetWorthByMonthAsync(Today);

        var month = Assert.Single(report.Months);
        Assert.Equal(Today, month.MonthEnd);
        Assert.Equal(300m, month.NetWorth);
    }

    [Fact]
    public async Task SplitsAssetsFromLiabilities()
    {
        var checking = await AccountAsync("Checking");
        var card = await AccountAsync("Card");
        await SnapshotAsync(checking, new(2026, 3, 1), 500m);
        await SnapshotAsync(card, new(2026, 3, 1), -200m);

        var month = Assert.Single((await _reports.GetNetWorthByMonthAsync(Today)).Months);

        Assert.Equal(500m, month.Assets);
        Assert.Equal(-200m, month.Liabilities);
        Assert.Equal(300m, month.NetWorth);
    }

    // A closed account's last snapshot is its closing balance. Carrying that forward would keep
    // it in net worth for good, which the dashboard doesn't do.
    [Fact]
    public async Task ClosedAccount_StopsCountingAfterItsLastSnapshotMonth()
    {
        var open = await AccountAsync("Checking");
        var closed = await AccountAsync("Old card", isClosed: true);
        await SnapshotAsync(open, new(2026, 1, 10), 100m);
        await SnapshotAsync(closed, new(2026, 1, 12), -30m);

        var report = await _reports.GetNetWorthByMonthAsync(Today);

        Assert.Equal(70m, report.Months[0].NetWorth);
        Assert.Equal(100m, report.Months[1].NetWorth);
        Assert.Equal(1, report.Months[1].AccountCount);
    }

    [Fact]
    public async Task StartsAtTheFirstMonthWithAnySnapshot()
    {
        var checking = await AccountAsync("Checking");
        await SnapshotAsync(checking, new(2026, 2, 14), 100m);

        var report = await _reports.GetNetWorthByMonthAsync(Today);

        Assert.Equal([new DateOnly(2026, 2, 28), Today], report.Months.Select(m => m.MonthEnd));
    }
}
