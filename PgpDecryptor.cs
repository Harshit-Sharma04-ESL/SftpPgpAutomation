
using PgpCore;
namespace SftpPgpAutomation;

public static class PgpDecryptor
{
    public static async Task DecryptFileAsync(string encryptedFilePath, string privateKeyPath, string passphrase, string outputDirectory)
    {
        if (!File.Exists(encryptedFilePath))
            throw new FileNotFoundException("Encrypted file not found.", encryptedFilePath);

        if (!File.Exists(privateKeyPath))
            throw new FileNotFoundException("PGP private key not found.", privateKeyPath);

        if (!encryptedFilePath.EndsWith(".pgp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Input file must have a .pgp extension.");

        Directory.CreateDirectory(outputDirectory);

        var outputFileName = Path.GetFileName(encryptedFilePath)[..^4];
        var outputPath = Path.Combine(outputDirectory, outputFileName);

        if (File.Exists(outputPath))
        {
            Console.WriteLine($"Skipped existing output: {outputFileName}");
            return;
        }

        var tempPath = Path.Combine(outputDirectory, $".{Guid.NewGuid():N}.tmp");

        try
        {
            Console.WriteLine($"Decrypting: {Path.GetFileName(encryptedFilePath)}");

            var keys = new EncryptionKeys(new FileInfo(privateKeyPath), passphrase);
            var pgp = new PGP(keys);

            await pgp.DecryptAsync(new FileInfo(encryptedFilePath), new FileInfo(tempPath));
            File.Move(tempPath, outputPath);

            Console.WriteLine($"Decrypted: {outputFileName}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Decryption failed: {ex.Message}");
            throw;
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
