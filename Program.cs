
using Microsoft.Extensions.Configuration;
using SftpPgpAutomation;

try
{
    var config = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
        .Build();

    string Required(string key) =>
        config[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Missing configuration: {key}");

    string RequiredEnv(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable {name} is not set.");

    var host = Required("Sftp:Host");
    var port = int.Parse(Required("Sftp:Port"));
    var username = Required("Sftp:Username");
    var remoteDirectory = Required("Sftp:RemoteDirectory");
    var fingerprint = Required("Sftp:ExpectedHostFingerprintSHA256");
    var sshKeyPath = Required("Sftp:SshPrivateKeyPath");
    var pgpKeyPath = Required("Pgp:PrivateKeyPath");
    var downloadDirectory = Required("Sftp:LocalDownloadDirectory");
    var outputDirectory = Required("Sftp:DecryptedOutputDirectory");

    //var sftpPassword = RequiredEnv("SFTP_PASSWORD");
    var sshPassphrase = Environment.GetEnvironmentVariable("SSH_KEY_PASSPHRASE") ?? "";
    var pgpPassphrase = Required("Pgp:Passphrase");

    if (!File.Exists(sshKeyPath))
    {
        Console.Error.WriteLine($"SSH private key not found: {sshKeyPath}");
        throw new FileNotFoundException("SSH private key not found.", sshKeyPath);
    }


    if (!File.Exists(pgpKeyPath))
    {
        Console.Error.WriteLine($"PGP private key not found: {pgpKeyPath}");
        throw new FileNotFoundException("PGP private key not found.", pgpKeyPath);
    }

    Directory.CreateDirectory(downloadDirectory);
    Directory.CreateDirectory(outputDirectory);

    Console.WriteLine("=== SFTP Download and PGP Decryption ===");

    string? encryptedFile = SftpDownloader.DownloadLatestFile(
    config["Sftp:Host"]!,
    int.Parse(config["Sftp:Port"]!),
    config["Sftp:Username"]!,
    config["Sftp:SshPrivateKeyPath"]!,
    config["Sftp:RemoteDirectory"]!,
    config["Sftp:LocalDownloadDirectory"]!);

    if (encryptedFile == null)
    {
        Console.WriteLine("No file downloaded. Processing stopped.");
        return;
    }

    await PgpDecryptor.DecryptFileAsync(
    encryptedFile,
    Required("Pgp:PrivateKeyPath"),
    Required("Pgp:Passphrase"),
    Required("Sftp:DecryptedOutputDirectory"));


    var encryptedFiles = Directory.GetFiles(
        downloadDirectory, "*.pgp", SearchOption.TopDirectoryOnly);

    if (encryptedFiles.Length == 0)
    {
        Console.WriteLine("No encrypted files available for decryption.");
        return;
    }

    var failedCount = 0;

    foreach (var file in encryptedFiles)
    {
        try
        {
            await PgpDecryptor.DecryptFileAsync(
                file,
                pgpKeyPath,
                pgpPassphrase,
                outputDirectory);
        }
        catch (Exception ex)
        {
            failedCount++;
            Console.Error.WriteLine(
                $"Failed: {Path.GetFileName(file)} - {ex.Message}");
        }
    }

    Console.WriteLine("\n=== Processing Summary ===");
    Console.WriteLine($"Encrypted files found: {encryptedFiles.Length}");
    Console.WriteLine($"Decryption failures: {failedCount}");
    Console.WriteLine($"Output folder: {outputDirectory}");
    Console.WriteLine("Processing finished.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Application error: {ex.Message}");
    Environment.ExitCode = 1;
}
