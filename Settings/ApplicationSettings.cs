namespace SftpPgpAutomation.Settings;

/// <summary>
/// SFTP configuration settings
/// </summary>
public class SftpSettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Username { get; set; } = string.Empty;
    public string RemoteDirectory { get; set; } = string.Empty;
    public string ExpectedHostFingerprintSHA256 { get; set; } = string.Empty;
    public string SshPrivateKeyPath { get; set; } = string.Empty;
    public string SshPrivateKeyPassphrase { get; set; } = string.Empty;
    public string LocalDownloadDirectory { get; set; } = string.Empty;
    public string DecryptedOutputDirectory { get; set; } = string.Empty;
}

/// <summary>
/// PGP configuration settings
/// </summary>
public class PgpSettings
{
    public string PrivateKeyPath { get; set; } = string.Empty;
    public string Passphrase { get; set; } = string.Empty;
}

/// <summary>
/// Logging configuration settings
/// </summary>
public class LoggingSettings
{
    public bool EnableExecutionLogging { get; set; } = true;
}

/// <summary>
/// Complete application configuration
/// </summary>
public class ApplicationSettings
{
    public SftpSettings Sftp { get; set; } = new();
    public PgpSettings Pgp { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
}
