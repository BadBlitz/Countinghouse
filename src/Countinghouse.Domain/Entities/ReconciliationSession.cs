namespace Countinghouse.Domain.Entities;

public class ReconciliationSession
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }

    public DateOnly StatementStartDate { get; set; }
    public DateOnly StatementEndDate { get; set; }
    public decimal StatementStartingBalance { get; set; }
    public decimal StatementEndingBalance { get; set; }

    public bool IsComplete { get; set; }
    public DateTime? CompletedAt { get; set; }

    public List<Transaction> Transactions { get; set; } = [];
}
