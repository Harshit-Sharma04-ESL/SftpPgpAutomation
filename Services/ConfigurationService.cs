using Microsoft.Extensions.Configuration;
using SftpPgpAutomation.Settings;

namespace SftpPgpAutomation.Services;

/// <summary>
/// Service for managing and validating application configuration
/// </summary>
public class ConfigurationService
{
    private readonly ApplicationSettings _settings;

    public SftpSettings Sftp => _settings.Sftp;
    public PgpSettings Pgp => _settings.Pgp;
    public LoggingSettings Logging => _settings.Logging;

    public string ConnectionString { get; }

    public ConfigurationService(IConfiguration configuration)
    {
        _settings = new ApplicationSettings();

        configuration.GetSection("Sftp").Bind(_settings.Sftp);
        configuration.GetSection("Pgp").Bind(_settings.Pgp);
        configuration.GetSection("Logging").Bind(_settings.Logging);

        ConnectionString = configuration.GetConnectionString("ExecutionLogsDb")
            ?? throw new InvalidOperationException("Connection string 'ExecutionLogsDb' not found in configuration");

        ValidateSettings();
    }

    /// <summary>
    /// Validates all required configuration settings
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when required settings are missing</exception>
    private void ValidateSettings()
    {
        var errors = new List<string>();

        // Validate SFTP settings
        if (string.IsNullOrWhiteSpace(_settings.Sftp.Host))
            errors.Add("SFTP Host is required");
        if (_settings.Sftp.Port <= 0)
            errors.Add("SFTP Port must be greater than 0");
        if (string.IsNullOrWhiteSpace(_settings.Sftp.Username))
            errors.Add("SFTP Username is required");
        if (string.IsNullOrWhiteSpace(_settings.Sftp.RemoteDirectory))
            errors.Add("SFTP RemoteDirectory is required");
        if (string.IsNullOrWhiteSpace(_settings.Sftp.SshPrivateKeyPath))
            errors.Add("SFTP SshPrivateKeyPath is required");
        if (string.IsNullOrWhiteSpace(_settings.Sftp.LocalDownloadDirectory))
            errors.Add("SFTP LocalDownloadDirectory is required");
        if (string.IsNullOrWhiteSpace(_settings.Sftp.DecryptedOutputDirectory))
            errors.Add("SFTP DecryptedOutputDirectory is required");

        // Validate PGP settings
        if (string.IsNullOrWhiteSpace(_settings.Pgp.PrivateKeyPath))
            errors.Add("PGP PrivateKeyPath is required");
        if (string.IsNullOrWhiteSpace(_settings.Pgp.Passphrase))
            errors.Add("PGP Passphrase is required");

        // Validate key files exist
        if (!File.Exists(_settings.Sftp.SshPrivateKeyPath))
            errors.Add($"SSH private key not found at: {_settings.Sftp.SshPrivateKeyPath}");
        if (!File.Exists(_settings.Pgp.PrivateKeyPath))
            errors.Add($"PGP private key not found at: {_settings.Pgp.PrivateKeyPath}");

        if (errors.Any())
        {
            var errorMessage = "Configuration validation failed:\n" + string.Join("\n", errors);
            throw new InvalidOperationException(errorMessage);
        }
    }

    /// <summary>
    /// Ensures required directories exist
    /// </summary>
    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(_settings.Sftp.LocalDownloadDirectory);
        Directory.CreateDirectory(_settings.Sftp.DecryptedOutputDirectory);
    }

    /// <summary>
    /// Gets the SSH passphrase from environment variable with fallback to empty string
    /// </summary>
    public string GetSshPassphrase() =>
        Environment.GetEnvironmentVariable("SSH_KEY_PASSPHRASE") ?? string.Empty;
}
