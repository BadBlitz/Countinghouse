namespace Countinghouse.Domain.Entities;

// One backup attempt, successful or not. Recorded in the database rather than inferred from
// whatever files happen to be on the destination, so the "nothing backed up in a week" reminder
// still works when the destination is unreachable — which is exactly when it matters most.
public class BackupRun
{
    public int Id { get; set; }
    public DateTime CompletedAt { get; set; }
    public string Destination { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }

    // Snapshot contents at the time, so a run is self-describing without opening the file.
    public int AccountCount { get; set; }
    public int TransactionCount { get; set; }

    public bool Succeeded { get; set; }
    public string? ErrorMessage { get; set; }
}
