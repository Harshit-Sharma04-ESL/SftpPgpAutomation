using Renci.SshNet;

namespace SftpPgpAutomation;

public static class SftpDownloader
{
    public static string? DownloadLatestFile(
        string host,
        int port,
        string username,
        string sshPrivateKeyPath,
        string remoteDirectory,
        string localDirectory)
    {
        if (!File.Exists(sshPrivateKeyPath))
        {
            throw new FileNotFoundException(
                "SSH private key not found.",
                sshPrivateKeyPath);
        }

        Directory.CreateDirectory(localDirectory);

        using var privateKey = new PrivateKeyFile(sshPrivateKeyPath);

        using var sftpClient = new SftpClient(
            host,
            port,
            username,
            new[] { privateKey });

        Console.WriteLine("Connecting to SFTP server...");

        sftpClient.Connect();

        try
        {
            Console.WriteLine("Connected to SFTP server successfully.");

            var files = sftpClient.ListDirectory(remoteDirectory);

            // Keep your existing filename pattern and latest-file selection here.
            // Example: nonAICRDetail_yyyy-MM-dd.csv.pgp

            var latestFile = files
                .Where(f => !f.IsDirectory && !f.IsSymbolicLink)
                .Select(f => new
                {
                    File = f,
                    Match = System.Text.RegularExpressions.Regex.Match(
                        f.Name,
                        @"^nonAICRDetail_(\d{4}-\d{2}-\d{2})\.csv\.pgp$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                })
                .Where(x => x.Match.Success)
                .Select(x => new
                {
                    x.File,
                    Date = DateTime.ParseExact(
                        x.Match.Groups[1].Value,
                        "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture)
                })
                .OrderByDescending(x => x.Date)
                .FirstOrDefault();

            if (latestFile == null)
            {
                Console.WriteLine("No matching files found.");
                return null;
            }

            string destination = Path.Combine(
                localDirectory,
                latestFile.File.Name);

            if (File.Exists(destination))
            {
                Console.WriteLine($"File already exists: {destination}");
                return destination;
            }

            using (var output = File.Create(destination))
            {
                sftpClient.DownloadFile(latestFile.File.FullName, output);
            }

            Console.WriteLine($"Downloaded successfully: {destination}");
            return destination;
        }
        finally
        {
            if (sftpClient.IsConnected)
                sftpClient.Disconnect();
        }
    }

    public static List<string> DownloadFilesNotStaged(
        string host,
        int port,
        string username,
        string sshPrivateKeyPath,
        string remoteDirectory,
        string localDirectory,
        IReadOnlySet<string> stagedFileNames)
    {
        if (!File.Exists(sshPrivateKeyPath))
        {
            throw new FileNotFoundException(
                "SSH private key not found.",
                sshPrivateKeyPath);
        }

        Directory.CreateDirectory(localDirectory);

        using var privateKey = new PrivateKeyFile(sshPrivateKeyPath);

        using var sftpClient = new SftpClient(
            host,
            port,
            username,
            new[] { privateKey });

        Console.WriteLine("Connecting to SFTP server...");

        sftpClient.Connect();

        try
        {
            Console.WriteLine("Connected to SFTP server successfully.");

            var remoteFiles = sftpClient.ListDirectory(remoteDirectory)
                .Where(f => !f.IsDirectory && !f.IsSymbolicLink)
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                    f.Name,
                    @"^nonAICRDetail_\d{4}-\d{2}-\d{2}\.csv\.pgp$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .OrderBy(f => f.Name)
                .ToList();

            var localFiles = new List<string>();

            foreach (var remoteFile in remoteFiles)
            {
                var sourceFileName = Path.GetFileNameWithoutExtension(remoteFile.Name);
                if (stagedFileNames.Contains(sourceFileName))
                {
                    Console.WriteLine($"Already staged in database, skipping download: {sourceFileName}");
                    continue;
                }

                var destination = Path.Combine(localDirectory, remoteFile.Name);
                if (!File.Exists(destination))
                {
                    using var output = File.Create(destination);
                    sftpClient.DownloadFile(remoteFile.FullName, output);
                    Console.WriteLine($"Downloaded successfully: {destination}");
                }

                localFiles.Add(destination);
            }

            return localFiles;
        }
        finally
        {
            if (sftpClient.IsConnected)
                sftpClient.Disconnect();
        }
    }
}