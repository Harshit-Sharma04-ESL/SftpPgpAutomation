using Microsoft.Extensions.Configuration;

namespace SftpPgpAutomation.Services;

/// <summary>
/// Coordinates the entire application execution workflow
/// </summary>
public class ExecutionCoordinator
{
    private readonly ConfigurationService _configService;
    private readonly FileProcessingService _fileProcessingService;
    private readonly LoggingService _loggingService;
    private readonly MailService _mailService;
    private readonly NuanceDownloadService _nuanceDownloadService;

    public ExecutionCoordinator(
        ConfigurationService configService,
        FileProcessingService fileProcessingService,
        LoggingService loggingService,
        MailService mailService,
        NuanceDownloadService nuanceDownloadService)
    {
        _configService = configService;
        _fileProcessingService = fileProcessingService;
        _loggingService = loggingService;
        _mailService = mailService;
        _nuanceDownloadService = nuanceDownloadService;
    }

    /// <summary>
    /// Executes the complete application workflow
    /// </summary>
    public async Task<int> ExecuteAsync()
    {
        var executionStartTime = DateTime.Now;
        string? executionError = null;
        FileProcessingResult? processingResult = null;

        try
        {
            _configService.EnsureDirectoriesExist();

            Console.WriteLine("\n" + new string('=', 60));
            Console.WriteLine("SFTP DOWNLOAD AND PGP DECRYPTION".PadRight(60));
            Console.WriteLine(new string('=', 60) + "\n");

            processingResult = await _fileProcessingService.ProcessFilesAsync();

            _fileProcessingService.DisplaySummary(processingResult);

            var engagementIds = NuanceDownloadService.GetSearchIdsFromLatestDownload(_configService.ConnectionString);
            await _nuanceDownloadService.DownloadAsync(NuanceDownloadService.LoginId, string.Join(", ", engagementIds));



            Console.WriteLine("\nProcessing completed successfully.\n");
            return 0;
        }
        catch (Exception ex)
        {
            executionError = ex.Message;
            Console.Error.WriteLine($"\n❌ Application error: {ex.Message}\n");
            return 1;
        }
        finally
        {
            await LogExecutionAsync(executionStartTime, executionError, processingResult);
            _mailService.SendExecutionNotification(
                isSuccess: executionError == null && processingResult?.IsSuccessful == true,
                errorMessage: executionError,
                result: processingResult,
                outputDirectory: _configService.Sftp.DecryptedOutputDirectory);
        }
    }

    /// <summary>
    /// Logs the execution to the database
    /// </summary>
    private async Task LogExecutionAsync(
        DateTime startTime,
        string? error,
        FileProcessingResult? result)
    {
        if (!_configService.Logging.EnableExecutionLogging)
        {
            return;
        }

        try
        {
            var endTime = DateTime.Now;
            var executionLog = new ExecutionLog
            {
                ExecutionStartTime = startTime,
                ExecutionEndTime = endTime,
                TotalFilesDownloaded = result?.TotalFilesFound ?? 0,
                TotalFilesDecrypted = result?.SuccessfullyDecrypted ?? 0,
                DecryptionFailures = result?.FailureCount ?? 0,
                ExecutionDuration = endTime - startTime,
                Status = DetermineStatus(error, result),
                ErrorMessage = error,
                RemoteDirectory = _configService.Sftp.RemoteDirectory,
                OutputDirectory = _configService.Sftp.DecryptedOutputDirectory
            };

            _loggingService.LogExecution(executionLog);

            DisplayExecutionLogs();
            Console.WriteLine("✓ Execution logged to MS SQL Server database.\n");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"⚠ Error logging execution: {ex.Message}\n");
        }
    }

    /// <summary>
    /// Determines the execution status based on results
    /// </summary>
    private string DetermineStatus(string? error, FileProcessingResult? result)
    {
        if (error != null)
            return "Failed";

        if (result?.IsSuccessful == true)
            return "Success";

        if (result?.IsPartialSuccess == true)
            return "PartialSuccess";

        return "Failed";
    }

    /// <summary>
    /// Displays recent execution logs
    /// </summary>
    private void DisplayExecutionLogs()
    {
        try
        {
            var recentLogs = _loggingService.GetRecentLogs(5);
            if (recentLogs.Any())
            {
                Console.WriteLine();
                _loggingService.DisplayLogsTable(recentLogs);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Could not display logs: {ex.Message}");
        }
    }
}
