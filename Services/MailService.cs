using System.Net;
using System.Net.Mail;
using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace SftpPgpAutomation.Services;

/// <summary>
/// Sends the execution status notification email
/// </summary>
public class MailService
{
    private readonly IConfigurationSection _mailSection;
    private readonly string _connectionString;

    public MailService(IConfiguration configuration, ConfigurationService configService)
    {
        _mailSection = configuration.GetSection("Mail");
        _connectionString = configService.ConnectionString;
    }

    /// <summary>
    /// Sends a notification with the execution status, the processing summary, and any error message
    /// </summary>
    public void SendExecutionNotification(bool isSuccess, string? errorMessage, FileProcessingResult? result, string outputDirectory)
    {
        if (!_mailSection.GetValue("Enabled", true))
        {
            return;
        }

        try
        {
            var status = isSuccess ? "Success" : "Failed";
            var subject = isSuccess ? _mailSection["SuccessSubject"] : _mailSection["FailureSubject"];
            var (fromAddress, toAddresses, ccAddresses) = LoadMailRecipients();
            var recipients = isSuccess ? toAddresses : (_mailSection["ErrorTo"] ?? toAddresses);

            using var message = new MailMessage
            {
                From = new MailAddress(fromAddress),
                Subject = $"{subject ?? "SFTP PGP Automation"} - {status} - {DateTime.Now:dd/MM/yyyy HH:mm}",
                Body = BuildBody(isSuccess, errorMessage, result, outputDirectory),
                IsBodyHtml = true,
                Priority = isSuccess ? MailPriority.Normal : MailPriority.High
            };

            foreach (var address in SplitAddresses(recipients))
                message.To.Add(address);
            foreach (var address in SplitAddresses(ccAddresses))
                message.CC.Add(address);

            if (message.To.Count == 0)
            {
                Console.Error.WriteLine("Mail:To is not configured. Notification not sent.");
                return;
            }

            using var smtp = new SmtpClient(_mailSection["SmtpServer"] ?? throw new InvalidOperationException("Mail:SmtpServer is not configured"))
            {
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Port = _mailSection.GetValue("Port", 25)
            };

            smtp.Send(message);
            Console.WriteLine("Notification email sent.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error sending notification email: {ex.Message}");
        }
    }

    private (string From, string To, string Cc) LoadMailRecipients()
    {
        using var connection = new SqlConnection(_connectionString);
        connection.Open();

        using var command = new SqlCommand("dbo.usp_getDailerUploadEmail", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("usp_getDailerUploadEmail returned no email configuration");

        return (
            Convert.ToString(reader.GetValue(0)) ?? string.Empty,
            Convert.ToString(reader.GetValue(1)) ?? string.Empty,
            Convert.ToString(reader.GetValue(2)) ?? string.Empty);
    }

    private static IEnumerable<MailAddress> SplitAddresses(string? addresses)
    {
        if (string.IsNullOrWhiteSpace(addresses))
            return Enumerable.Empty<MailAddress>();

        return addresses
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => new MailAddress(a));
    }

    private static string BuildBody(bool isSuccess, string? errorMessage, FileProcessingResult? result, string outputDirectory)
    {
        var sb = new StringBuilder();
        sb.Append("Hello Team,<br><br>");
        sb.Append(isSuccess
            ? "The SFTP download and PGP decryption completed successfully.<br><br>"
            : "The SFTP download and PGP decryption <b>failed</b>. Kindly check the details below.<br><br>");

        sb.Append("<table border='1' cellpadding='5' cellspacing='0' style='font-size: small;font-family:Tahoma'>");
        AppendRow(sb, "Total Files Found", result?.TotalFilesFound.ToString() ?? "0");
        AppendRow(sb, "Successfully Decrypted", result?.SuccessfullyDecrypted.ToString() ?? "0");
        AppendRow(sb, "Decryption Failures", result?.FailureCount.ToString() ?? "0");
        AppendRow(sb, "Output Directory", outputDirectory);
        sb.Append("</table><br>");

        if (result?.FailedFiles.Count > 0)
        {
            sb.Append("<b>Failed Files:</b><br>");
            sb.Append(string.Join("<br>", result.FailedFiles.Select(System.Net.WebUtility.HtmlEncode)));
            sb.Append("<br><br>");
        }

        if (!string.IsNullOrEmpty(errorMessage))
        {
            sb.Append("<b>Error:</b> ");
            sb.Append(System.Net.WebUtility.HtmlEncode(errorMessage));
            sb.Append("<br><br>");
        }

        sb.Append("<div style='font-size: x-small;color:red;font-family:Calibri'>");
        sb.Append("THIS IS SYSTEM NOTIFICATION. DO NOT REPLY TO THIS EMAIL!</div>");
        sb.Append("<br><b>Thanks,<br>Software Team</b>");
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, string label, string value)
    {
        sb.Append("<tr><td style='font-weight:bold'>");
        sb.Append(System.Net.WebUtility.HtmlEncode(label));
        sb.Append("</td><td>");
        sb.Append(System.Net.WebUtility.HtmlEncode(value));
        sb.Append("</td></tr>");
    }
}
