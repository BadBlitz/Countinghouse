using Countinghouse.Domain.Entities;
using Countinghouse.Domain.Services;

namespace Countinghouse.Tests;

public class MovementExpectationSuggesterTests
{
    private static readonly DateOnly Today = new(2026, 9, 6);

    private static Account Account(
        AccountType type = AccountType.Checking,
        bool? syncTransactionsOverride = null) => new()
    {
        Id = 1,
        Name = "Account",
        AccountType = type,
        SyncTransactionsOverride = syncTransactionsOverride,
    };

    private static List<DateOnly> Dates(params string[] dates) =>
        dates.Select(DateOnly.Parse).OrderBy(d => d).ToList();

    // Investment accounts keep no register, so their cadence can't be measured — but it doesn't
    // need to be. They move on market value, every trading day, which is exactly the expectation
    // their (nonexistent) transaction history could never have revealed.
    [Fact]
    public void MarketDays_ForAnInvestmentAccountWithNoRegister()
    {
        var suggestion = MovementExpectationSuggester.Suggest(
            Account(AccountType.Investment), [], Today);

        Assert.Equal(MovementExpectation.MarketDays, suggestion.Suggested);
        Assert.Null(suggestion.SuggestedDays);
    }

    [Fact]
    public void None_ForABalanceOnlyAccountThatIsNotAnInvestment()
    {
        var suggestion = MovementExpectationSuggester.Suggest(
            Account(AccountType.Savings, syncTransactionsOverride: false), [], Today);

        Assert.Equal(MovementExpectation.None, suggestion.Suggested);
        Assert.Contains("no history to measure", suggestion.Rationale);
    }

    // A card paid off and left at zero produces a confident "never moves" that is true and
    // useless. Too few movements means no proposal at all rather than a fabricated one.
    [Fact]
    public void None_WhenThereAreTooFewMovementsToMeasure()
    {
        var suggestion = MovementExpectationSuggester.Suggest(
            Account(AccountType.CreditCard), Dates("2026-03-01", "2026-03-15"), Today);

        Assert.Equal(MovementExpectation.None, suggestion.Suggested);
        Assert.Equal(2, suggestion.SampleCount);
        Assert.Contains("too few", suggestion.Rationale);
    }

    // A savings account whose interest posts on the 21st and nothing else. The longest quiet stretch
    // is the month between postings, so the window has to clear it or the indicator cries wolf
    // for four weeks out of every five.
    [Fact]
    public void WithinDays_FromTheLongestQuietStretch()
    {
        var suggestion = MovementExpectationSuggester.Suggest(
            Account(AccountType.Savings),
            Dates("2026-06-21", "2026-07-21", "2026-08-20", "2026-09-05"),
            Today);

        Assert.Equal(MovementExpectation.WithinDays, suggestion.Suggested);
        Assert.Equal(30, suggestion.LongestObservedGap);
        Assert.Equal(30 + MovementExpectationSuggester.Margin, suggestion.SuggestedDays);
    }

    // A checking account: a deposit every fortnight, plus everything else. The gaps between postings are
    // short, so the suggestion is tight — which is what makes it a useful detector there.
    [Fact]
    public void WithinDays_IsTightForAnAccountInRegularUse()
    {
        var suggestion = MovementExpectationSuggester.Suggest(
            Account(),
            Dates("2026-08-21", "2026-08-24", "2026-08-28", "2026-09-04", "2026-09-05"),
            Today);

        Assert.Equal(7, suggestion.LongestObservedGap);
        Assert.Equal(11, suggestion.SuggestedDays);
    }

    // An account that posted weekly all year and then stopped six weeks ago has a longest gap of
    // six weeks. Measuring only the gaps *between* postings would hide the stretch worth noticing
    // and propose a window the account is already violating.
    [Fact]
    public void WithinDays_CountsTheStretchStillRunning()
    {
        var suggestion = MovementExpectationSuggester.Suggest(
            Account(),
            Dates("2026-07-01", "2026-07-08", "2026-07-15", "2026-07-22"),
            Today);

        Assert.Equal(46, suggestion.LongestObservedGap);
        Assert.Contains("still running", suggestion.Rationale);
    }

    [Fact]
    public void DiffersFromCurrent_IsFalseOnceTheSuggestionHasBeenAccepted()
    {
        var account = Account(AccountType.Savings);
        account.ExpectedMovement = MovementExpectation.WithinDays;
        account.ExpectedMovementDays = 34;

        var suggestion = MovementExpectationSuggester.Suggest(
            account, Dates("2026-06-21", "2026-07-21", "2026-08-20", "2026-09-05"), Today);

        Assert.Equal(34, suggestion.SuggestedDays);
        Assert.False(suggestion.DiffersFromCurrent);
    }
}
