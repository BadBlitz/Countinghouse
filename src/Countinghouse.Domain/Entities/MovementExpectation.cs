namespace Countinghouse.Domain.Entities;

// How often an account's balance is expected to change, which is what makes a feed that has
// quietly died distinguishable from an account that is simply quiet.
//
// This can't be derived from the account type. Savings #1 and Savings #2 are both
// savings accounts, but the first moves once a month on an interest posting and the second sat
// unchanged for a fortnight and then went to zero. The expectation is a fact about how the account
// is *used*, so it is set per account — suggested from the register by
// MovementExpectationSuggester, and confirmed by the user, the same way category rules are
// proposed rather than applied.
public enum MovementExpectation
{
    // No expectation. The default, and the right answer for a dormant account: Main Bank Credit Card
    // sits at $0.00 indefinitely, so "hasn't moved" says nothing about whether its feed is alive.
    None = 0,

    // Should change on every trading day. The investment accounts, which move on market value
    // rather than on transactions.
    //
    // Evaluated only against observations dated on a weekday, because weekend observations are
    // not trustworthy: a brokerage feed can re-serve Friday's balance unchanged over the weekend
    // on some accounts while others in the same sync move normally. Comparing across those would
    // report "no movement" every weekend on the accounts holding most of the net worth.
    MarketDays = 1,

    // Should change at least once within ExpectedMovementDays. Covers everything driven by
    // transactions rather than market value: a salary every fortnight, interest once a month, a
    // credit card in regular use.
    WithinDays = 2,
}
