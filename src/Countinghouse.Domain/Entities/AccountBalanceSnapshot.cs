namespace Countinghouse.Domain.Entities;

// One row per account per day, written on every sync/manual balance update.
// Source data for the net-worth-over-time report.
public class AccountBalanceSnapshot
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    public DateOnly AsOfDate { get; set; }
    public decimal Balance { get; set; }
}
