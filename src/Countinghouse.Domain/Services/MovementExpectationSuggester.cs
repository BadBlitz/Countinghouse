using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;
using Countinghouse.Domain.Entities;

namespace Countinghouse.Domain.Services;

public sealed record MovementSuggestion
{
    public required int AccountId { get; init; }
    public required string AccountName { get; init; }

    public required MovementExpectation Current { get; init; }
    public int? CurrentDays { get; init; }

    public required MovementExpectation Suggested { get; init; }
    public int? SuggestedDays { get; init; }

    // The longest the balance has actually gone without moving, over the window considered. The
    // suggestion is this plus a margin, and it is shown because it is the figure worth arguing
    // with — a window tighter than an account's own observed quiet stretch will cry wolf.
    public int? LongestObservedGap { get; init; }

    // How many balance-moving days were found. A suggestion drawn from three of them is a guess.
    public required int SampleCount { get; init; }

    public required string Rationale { get; init; }

    public bool DiffersFromCurrent =>
        Suggested != Current || (Suggested == MovementExpectation.WithinDays && SuggestedDays != CurrentDays);
}

// Proposes a movement expectation per account by measuring how the account has actually behaved,
// rather than asking the user to invent a number.
//
// Proposed, never applied — the same stance CategoryRuleService.SuggestFromHistoryAsync takes, and
// for the same reason: the history says what an account has done, not what it is for. A card paid
// off last year and left at zero produces a confident "never moves" that is true and useless, and
// only the account holder knows which of those it is.
public class MovementExpectationSuggester(IDbContextFactory<CountinghouseContext> factory)
{
    // How far back to measure. A year takes in the seasonal quiet stretches — the fortnight around
    // Christmas where nothing posts — that a three-month window would miss and then fire on.
    public const int WindowDays = 365;

    // Added to the longest observed gap to get the suggested window. Small on purpose: the gap is
    // already a maximum, so this only covers the posting landing a few days later than usual.
    public const int Margin = 4;

    // Below this, the account has not moved enough for a gap to mean anything.
    public const int MinSamples = 3;

    public async Task<List<MovementSuggestion>> SuggestAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => !a.IsClosed && a.SimpleFinConnectionId != null)
            .OrderBy(a => a.Name)
            .ToListAsync(cancellationToken);

        var earliest = DateOnly.FromDateTime(DateTime.Today).AddDays(-WindowDays);
        var accountIds = accounts.Select(a => a.Id).ToList();

        // Distinct posting dates are what matter: two transactions on one day are one movement of
        // the balance, and counting them separately would understate every gap.
        var movementDates = await db.Transactions.AsNoTracking()
            .Where(t => accountIds.Contains(t.AccountId) && t.PostedDate >= earliest)
            .Select(t => new { t.AccountId, t.PostedDate })
            .Distinct()
            .ToListAsync(cancellationToken);

        var datesByAccount = movementDates
            .GroupBy(t => t.AccountId)
            .ToDictionary(g => g.Key, g => g.Select(t => t.PostedDate).OrderBy(d => d).ToList());

        var today = DateOnly.FromDateTime(DateTime.Today);
        return accounts
            .Select(a => Suggest(a, datesByAccount.GetValueOrDefault(a.Id) ?? [], today))
            .ToList();
    }

    internal static MovementSuggestion Suggest(Account account, List<DateOnly> movementDates, DateOnly today)
    {
        var baseline = new MovementSuggestion
        {
            AccountId = account.Id,
            AccountName = account.Name,
            Current = account.ExpectedMovement,
            CurrentDays = account.ExpectedMovementDays,
            Suggested = MovementExpectation.None,
            SampleCount = movementDates.Count,
            Rationale = "",
        };

        // An account that keeps no register can't be measured this way, and for the investment
        // accounts it doesn't need to be: they move on market value, every trading day, which is
        // an expectation their transaction history could never have shown (they have none).
        if (!account.SyncsTransactions)
        {
            return account.AccountType == AccountType.Investment
                ? baseline with
                {
                    Suggested = MovementExpectation.MarketDays,
                    Rationale = "Tracks market value and keeps no register, so it should move on "
                                + "every trading day.",
                }
                : baseline with
                {
                    Rationale = "Tracks a balance only and keeps no register, so there is no "
                                + "history to measure a cadence from.",
                };
        }

        if (movementDates.Count < MinSamples)
        {
            return baseline with
            {
                Rationale = $"Only {movementDates.Count} balance movements in the last "
                            + $"{WindowDays} days — too few to tell a quiet account from a dead "
                            + "feed, so no expectation is proposed.",
            };
        }

        // The gap from the last movement to today counts. An account that posted weekly all year
        // and then stopped six weeks ago has a longest gap of six weeks, and a suggestion drawn
        // only from the gaps *between* postings would hide exactly the stretch worth noticing.
        var longest = 0;
        for (var i = 1; i < movementDates.Count; i++)
            longest = Math.Max(longest, movementDates[i].DayNumber - movementDates[i - 1].DayNumber);
        var trailing = today.DayNumber - movementDates[^1].DayNumber;
        longest = Math.Max(longest, trailing);

        var suggested = longest + Margin;
        return baseline with
        {
            Suggested = MovementExpectation.WithinDays,
            SuggestedDays = suggested,
            LongestObservedGap = longest,
            Rationale = $"{movementDates.Count} balance movements in the last {WindowDays} days, "
                        + $"the longest quiet stretch being {longest} days"
                        + (trailing == longest ? " — which is the stretch still running" : "")
                        + $". Suggesting {suggested} days, {Margin} days of margin above it.",
        };
    }
}
