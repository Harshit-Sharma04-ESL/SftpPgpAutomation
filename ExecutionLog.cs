namespace SftpPgpAutomation;

/// <summary>
/// Represents an execution log entry for SFTP download and PGP decryption operations.
/// </summary>
public class ExecutionLog
{
    public int Id { get; set; }
    public DateTime ExecutionStartTime { get; set; }
    public DateTime ExecutionEndTime { get; set; }
    public int TotalFilesDownloaded { get; set; }
    public int TotalFilesDecrypted { get; set; }
    public int DecryptionFailures { get; set; }
    public TimeSpan ExecutionDuration { get; set; }
    public string Status { get; set; } = "Success"; // Success, PartialSuccess, Failed
    public string? ErrorMessage { get; set; }
    public string? RemoteDirectory { get; set; }
    public string? OutputDirectory { get; set; }
    public decimal SuccessRate => TotalFilesDecrypted + DecryptionFailures > 0
        ? (decimal)TotalFilesDecrypted / (TotalFilesDecrypted + DecryptionFailures) * 100
        : 0;
}
