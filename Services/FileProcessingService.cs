namespace SftpPgpAutomation.Services;

/// <summary>
/// Result of file processing operation
/// </summary>
public class FileProcessingResult
{
    public int TotalFilesFound { get; set; }
    public int SuccessfullyDecrypted { get; set; }
    public int FailureCount { get; set; }
    public List<string> FailedFiles { get; set; } = new();

    public bool IsSuccessful => FailureCount == 0;
    public bool IsPartialSuccess => FailureCount > 0 && SuccessfullyDecrypted > 0;
}

/// <summary>
/// Service for orchestrating SFTP download and PGP decryption operations
/// </summary>
public class FileProcessingService
{
    private const int RetentionDays = 15;

    private readonly ConfigurationService _configService;
    private readonly ExcelStagingService _excelStagingService;

    public FileProcessingService(ConfigurationService configService, ExcelStagingService excelStagingService)
    {
        _configService = configService;
        _excelStagingService = excelStagingService;
    }

    /// <summary>
    /// Executes the complete file download and decryption workflow
    /// </summary>
    public async Task<FileProcessingResult> ProcessFilesAsync()
    {
        var result = new FileProcessingResult();

        try
        {
            DeleteExpiredOutputFiles();

            // Step 1: Check staging table for files already loaded, then download the rest from SFTP
            var stagedFileNames = _excelStagingService.GetStagedSourceFileNames();
            Console.WriteLine($"Source files already staged in database: {stagedFileNames.Count}");

            Console.WriteLine("Downloading files from SFTP server...");
            var downloadedFiles = SftpDownloader.DownloadFilesNotStaged(
                _configService.Sftp.Host,
                _configService.Sftp.Port,
                _configService.Sftp.Username,
                _configService.Sftp.SshPrivateKeyPath,
                _configService.Sftp.RemoteDirectory,
                _configService.Sftp.LocalDownloadDirectory,
                stagedFileNames);

            // Step 2: Process every downloaded file that is not yet staged
            var encryptedFiles = downloadedFiles.ToArray();

            result.TotalFilesFound = encryptedFiles.Length;

            if (encryptedFiles.Length == 0)
            {
                Console.WriteLine("No new encrypted files available for decryption.");
                return result;
            }

            // Step 3: Decrypt all files
            Console.WriteLine($"Decrypting {encryptedFiles.Length} file(s)...\n");
            await DecryptFilesAsync(encryptedFiles, result);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error during file processing: {ex.Message}");
            throw;
        }

        return result;
    }

    /// <summary>
    /// Deletes files in the shared output directory that are older than the retention period
    /// </summary>
    private void DeleteExpiredOutputFiles()
    {
        var outputDirectory = _configService.Sftp.DecryptedOutputDirectory;
        if (!Directory.Exists(outputDirectory))
            return;

        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        foreach (var file in Directory.GetFiles(outputDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                File.Delete(file);
                Console.WriteLine($"Deleted expired file (older than {RetentionDays} days): {Path.GetFileName(file)}");
            }
        }
    }

    /// <summary>
    /// Decrypts all files and updates the result
    /// </summary>
    private async Task DecryptFilesAsync(string[] files, FileProcessingResult result)
    {
        foreach (var file in files)
        {
            try
            {
                Console.WriteLine($"Decrypting: {Path.GetFileName(file)}");
                await PgpDecryptor.DecryptFileAsync(
                    file,
                    _configService.Pgp.PrivateKeyPath,
                    _configService.Pgp.Passphrase,
                    _configService.Sftp.DecryptedOutputDirectory);

                result.SuccessfullyDecrypted++;
                Console.WriteLine($"  ✓ Successfully decrypted");

                var decryptedFile = Path.Combine(
                    _configService.Sftp.DecryptedOutputDirectory,
                    Path.GetFileName(file)[..^4]);
                _excelStagingService.ProcessExcelFile(decryptedFile);

                File.Delete(file);
            }
            catch (Exception ex)
            {
                result.FailureCount++;
                result.FailedFiles.Add(Path.GetFileName(file));
                Console.Error.WriteLine($"  ✗ Failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Displays the processing summary
    /// </summary>
    public void DisplaySummary(FileProcessingResult result)
    {
        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine("PROCESSING SUMMARY".PadRight(60));
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"Total Files Found:        {result.TotalFilesFound}");
        Console.WriteLine($"Successfully Decrypted:   {result.SuccessfullyDecrypted}");
        Console.WriteLine($"Decryption Failures:      {result.FailureCount}");
        Console.WriteLine($"Output Directory:         {_configService.Sftp.DecryptedOutputDirectory}");

        if (result.FailedFiles.Any())
        {
            Console.WriteLine("\nFailed Files:");
            foreach (var file in result.FailedFiles)
            {
                Console.WriteLine($"  - {file}");
            }
        }

        Console.WriteLine(new string('=', 60));
    }
}
