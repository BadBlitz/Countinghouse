namespace Countinghouse.Domain.Entities;

public class Account
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public AccountType AccountType { get; set; }

    public decimal CurrentBalance { get; set; }
    public DateTime? BalanceAsOf { get; set; }
    public decimal OpeningBalance { get; set; }
    public DateOnly? OpeningBalanceDate { get; set; }
    public bool IsClosed { get; set; }

    public int? SimpleFinConnectionId { get; set; }
    public SimpleFinConnection? SimpleFinConnection { get; set; }
    public string? SimpleFinAccountId { get; set; }

    // What the last sync reported against this account's institution, or null when it reported
    // nothing. One Access URL spans several institutions and a failure at one arrives as a
    // connection-wide error, so without this every account on the connection reads as failing.
    public string? LastSyncError { get; set; }

    // Null means "decide from the account type", which is what almost every account should use.
    // Set it only to override that decision for one specific account.
    public bool? SyncTransactionsOverride { get; set; }

    // How often this account's balance is expected to change, and — for WithinDays — the window
    // it should change within. See MovementExpectation: this is what separates "the feed is dead"
    // from "the account is quiet", which no amount of looking at the balance alone can tell apart.
    public MovementExpectation ExpectedMovement { get; set; } = MovementExpectation.None;
    public int? ExpectedMovementDays { get; set; }

    // A WithinDays expectation with no window set is not an expectation, so it never fires.
    public bool HasMovementExpectation =>
        ExpectedMovement == MovementExpectation.MarketDays
        || (ExpectedMovement == MovementExpectation.WithinDays && ExpectedMovementDays > 0);

    public List<Transaction> Transactions { get; set; } = [];

    public bool IsSynced => SimpleFinConnectionId is not null;

    // Investment accounts sync a balance and nothing else.
    //
    // Their feeds report share movements, not cash flows: a plan contribution arrives as cash and
    // is immediately invested, so the same event appears as two opposing legs, and SimpleFIN can
    // surface them inconsistently — sign-flipped, inverted, or with one leg missing entirely. A
    // register built from that is worse than no register, and the balance is authoritative anyway
    // since it comes straight from the API.
    //
    // This is the same reasoning that makes QifImportService refuse !Type:Invst and
    // AccountSetupService mark all five Brokerage accounts snapshot-only.
    public bool SyncsTransactions =>
        SyncTransactionsOverride ?? AccountType != AccountType.Investment;
}
