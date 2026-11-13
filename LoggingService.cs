using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace SftpPgpAutomation;

/// <summary>
/// Service for managing execution logs in MS SQL Server database.
/// </summary>
public class LoggingService
{
    private readonly string _connectionString;

    public LoggingService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("ExecutionLogsDb")
            ?? throw new InvalidOperationException("Connection string 'ExecutionLogsDb' not found in configuration");
        InitializeDatabase();
    }

    /// <summary>
    /// Initializes the MS SQL Server database and creates the ExecutionLogs table if it doesn't exist.
    /// </summary>
    private void InitializeDatabase()
    {
        using var connection = new SqlConnection(_connectionString);
        connection.Open();

        const string createTableSql = @"
            IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'ExecutionLogs')
            BEGIN
                CREATE TABLE tbl_ExecutionLogs (
                    Id INT PRIMARY KEY IDENTITY(1,1),
                    ExecutionStartTime DATETIME2 NOT NULL,
                    ExecutionEndTime DATETIME2 NOT NULL,
                    TotalFilesDownloaded INT NOT NULL DEFAULT 0,
                    TotalFilesDecrypted INT NOT NULL DEFAULT 0,
                    DecryptionFailures INT NOT NULL DEFAULT 0,
                    ExecutionDurationMilliseconds BIGINT NOT NULL,
                    Status NVARCHAR(50) NOT NULL CHECK(Status IN ('Success', 'PartialSuccess', 'Failed')),
                    ErrorMessage NVARCHAR(MAX),
                    RemoteDirectory NVARCHAR(500),
                    OutputDirectory NVARCHAR(500),
                    CreatedAt DATETIME2 DEFAULT GETUTCDATE()
                );

                CREATE INDEX IX_ExecutionLogs_ExecutionStartTime ON ExecutionLogs(ExecutionStartTime DESC);
                CREATE INDEX IX_ExecutionLogs_Status ON ExecutionLogs(Status);
                CREATE INDEX IX_ExecutionLogs_CreatedAt ON ExecutionLogs(CreatedAt DESC);
            END
        ";

        using var command = new SqlCommand(createTableSql, connection);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Logs an execution to the database.
    /// </summary>
    public void LogExecution(ExecutionLog log)
    {
        using var connection = new SqlConnection(_connectionString);
        connection.Open();

        const string insertSql = @"
            INSERT INTO tbl_ExecutionLogs (
                ExecutionStartTime, 
                ExecutionEndTime, 
                TotalFilesDownloaded, 
                TotalFilesDecrypted, 
                DecryptionFailures, 
                ExecutionDurationMilliseconds, 
                Status, 
                ErrorMessage, 
                RemoteDirectory, 
                OutputDirectory
            ) VALUES (
                @startTime, 
                @endTime, 
                @filesDownloaded, 
                @filesDecrypted, 
                @failures, 
                @duration, 
                @status, 
                @errorMessage, 
                @remoteDir, 
                @outputDir
            );
        ";

        using var command = new SqlCommand(insertSql, connection);
        command.Parameters.AddWithValue("@startTime", log.ExecutionStartTime);
        command.Parameters.AddWithValue("@endTime", log.ExecutionEndTime);
        command.Parameters.AddWithValue("@filesDownloaded", log.TotalFilesDownloaded);
        command.Parameters.AddWithValue("@filesDecrypted", log.TotalFilesDecrypted);
        command.Parameters.AddWithValue("@failures", log.DecryptionFailures);
        command.Parameters.AddWithValue("@duration", (long)log.ExecutionDuration.TotalMilliseconds);
        command.Parameters.AddWithValue("@status", log.Status);
        command.Parameters.AddWithValue("@errorMessage", (object?)log.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("@remoteDir", (object?)log.RemoteDirectory ?? DBNull.Value);
        command.Parameters.AddWithValue("@outputDir", (object?)log.OutputDirectory ?? DBNull.Value);

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Retrieves all execution logs.
    /// </summary>
    public List<ExecutionLog> GetAllLogs()
    {
        var logs = new List<ExecutionLog>();

        using var connection = new SqlConnection(_connectionString);
        connection.Open();

        const string selectSql = @"
            SELECT 
                Id, 
                ExecutionStartTime, 
                ExecutionEndTime, 
                TotalFilesDownloaded, 
                TotalFilesDecrypted, 
                DecryptionFailures, 
                ExecutionDurationMilliseconds, 
                Status, 
                ErrorMessage, 
                RemoteDirectory, 
                OutputDirectory
            FROM tbl_ExecutionLogs
            ORDER BY ExecutionStartTime DESC;
        ";

        using var command = new SqlCommand(selectSql, connection);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            logs.Add(new ExecutionLog
            {
                Id = reader.GetInt32(0),
                ExecutionStartTime = reader.GetDateTime(1),
                ExecutionEndTime = reader.GetDateTime(2),
                TotalFilesDownloaded = reader.GetInt32(3),
                TotalFilesDecrypted = reader.GetInt32(4),
                DecryptionFailures = reader.GetInt32(5),
                ExecutionDuration = TimeSpan.FromMilliseconds(reader.GetInt64(6)),
                Status = reader.GetString(7),
                ErrorMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
                RemoteDirectory = reader.IsDBNull(9) ? null : reader.GetString(9),
                OutputDirectory = reader.IsDBNull(10) ? null : reader.GetString(10)
            });
        }

        return logs;
    }

    /// <summary>
    /// Retrieves recent execution logs (last N records).
    /// </summary>
    public List<ExecutionLog> GetRecentLogs(int count = 10)
    {
        var logs = new List<ExecutionLog>();

        using var connection = new SqlConnection(_connectionString);
        connection.Open();

        const string selectSql = @"
            SELECT TOP (@count)
                Id, 
                ExecutionStartTime, 
                ExecutionEndTime, 
                TotalFilesDownloaded, 
                TotalFilesDecrypted, 
                DecryptionFailures, 
                ExecutionDurationMilliseconds, 
                Status, 
                ErrorMessage, 
                RemoteDirectory, 
                OutputDirectory
            FROM tbl_ExecutionLogs
            ORDER BY ExecutionStartTime DESC;
        ";

        using var command = new SqlCommand(selectSql, connection);
        command.Parameters.AddWithValue("@count", count);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            logs.Add(new ExecutionLog
            {
                Id = reader.GetInt32(0),
                ExecutionStartTime = reader.GetDateTime(1),
                ExecutionEndTime = reader.GetDateTime(2),
                TotalFilesDownloaded = reader.GetInt32(3),
                TotalFilesDecrypted = reader.GetInt32(4),
                DecryptionFailures = reader.GetInt32(5),
                ExecutionDuration = TimeSpan.FromMilliseconds(reader.GetInt64(6)),
                Status = reader.GetString(7),
                ErrorMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
                RemoteDirectory = reader.IsDBNull(9) ? null : reader.GetString(9),
                OutputDirectory = reader.IsDBNull(10) ? null : reader.GetString(10)
            });
        }

        return logs;
    }

    /// <summary>
    /// Displays execution logs in a formatted table.
    /// </summary>
    public void DisplayLogsTable(List<ExecutionLog> logs)
    {
        if (logs.Count == 0)
        {
            Console.WriteLine("No execution logs found.");
            return;
        }

        Console.WriteLine("\n=== Execution Logs Table ===\n");
        Console.WriteLine("{0,-5} {1,-19} {2,-19} {3,-12} {4,-12} {5,-10} {6,-10} {7,-15}",
            "ID", "Start Time", "End Time", "Downloaded", "Decrypted", "Failures", "Duration", "Status");
        Console.WriteLine(new string('-', 130));

        foreach (var log in logs)
        {
            Console.WriteLine("{0,-5} {1,-19} {2,-19} {3,-12} {4,-12} {5,-10} {6,-10} {7,-15}",
                log.Id,
                log.ExecutionStartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                log.ExecutionEndTime.ToString("yyyy-MM-dd HH:mm:ss"),
                log.TotalFilesDownloaded,
                log.TotalFilesDecrypted,
                log.DecryptionFailures,
                $"{log.ExecutionDuration.TotalSeconds:F2}s",
                log.Status);
        }

        Console.WriteLine(new string('-', 130));
    }
}
