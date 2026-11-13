using Microsoft.Data.SqlClient;
using Microsoft.Playwright;

namespace SftpPgpAutomation.Services;

/// <summary>
/// Downloads Nuance transcript exports using Playwright browser automation
/// </summary>
public class NuanceDownloadService
{
    private const string TopFilterRemoveButtonXPath = "//*[@id='main']/div[2]/query-builder-rt/div/div[2]/div/abstract-query-builder/div[1]/div[3]/query-filter/div/div[2]/div[1]/div/div/div/button";
    private const string SalesQualifiedRemoveButtonXPath = "//*[@id='main']/div[2]/query-builder-rt/div/div[2]/div/abstract-query-builder/div[1]/div[3]/query-filter/div/div[2]/div/div/div/div/button";
    private const string InstantExportOptionXPath = "//*[@id='main']/div[2]/query-builder-rt/div/div[2]/div/abstract-query-builder/div[2]/form/div[2]/div[1]/ul/li[5]/a";
    private const string SecondExportOptionXPath = "/html/body/div[2]/div[2]/query-builder-rt/div/div[2]/div/abstract-query-builder/div[2]/form/div[2]/div[1]/ul/li[3]/a";
    private const string ItemListInputXPath = "/html/body/div[2]/div[2]/query-builder-rt/div/div[2]/div/abstract-query-builder/div[1]/div[4]/item-list/div/div/div[2]/input";
    private const string ItemListSuggestionXPathFormat = "//item-list//button[contains(@class,'btn-transparent') and normalize-space(.)='{0}']";
    public const string LoginId = "bp-rgauta233@cable.comcast.com";
    private const string ExportFolderPath = @"C:\nuissance automation";
    private const string NuanceSsoUrl = "https://portal.xchat.xfinity.com/portal/sso/portal?IdPId=13";
    private const string TranscriptSearchUrl = "https://portal.xchat.xfinity.com/portal/n/transcriptSearchRT/10006690?locale=en";
    private static readonly string[] ItemListValues = { "Agent Name", "Engagement Attribute:Account", "Engagement Duration", "Final Owning Agent", "Engagement Attribute:ChannelChatStartTime", "Final Owning Agent Attribute:AgentID" };

    /// <summary>
    /// Reads CHAT_ACTIVITY_ID and VIOLATING_CHAT_ACTIVITY_ID from the latest LoadedAt batch in dbo.AICR_Final
    /// </summary>
    public static List<string> GetSearchIdsFromLatestDownload(string connectionString)
    {
        const string sql = @"
SELECT DISTINCT ids.Id
FROM dbo.AICR_Final AS f
CROSS APPLY (VALUES (f.CHAT_ACTIVITY_ID), (f.VIOLATING_CHAT_ACTIVITY_ID)) AS ids(Id)
WHERE f.LoadedAt = (SELECT MAX(LoadedAt) FROM dbo.AICR_Final)
  AND NULLIF(LTRIM(RTRIM(ids.Id)), '') IS NOT NULL;";

        var ids = new List<string>();
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0).Trim());
        }

        Console.WriteLine($"Read {ids.Count} search ID(s) from dbo.AICR_Final (latest LoadedAt)");
        return ids;
    }

    /// <summary>
    /// Logs in to Nuance, runs both transcript exports and saves the files
    /// </summary>
    public async Task<IReadOnlyList<string>> DownloadAsync(string loginId, string engagementIds)
    {
        var savedFiles = new List<string>();
        IPlaywright? playwright = null;
        IBrowser? browser = null;

        try
        {
            Directory.CreateDirectory(ExportFolderPath);

            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
                SlowMo = 100
            });

            var context = await browser.NewContextAsync(new BrowserNewContextOptions { AcceptDownloads = true });
            //Log.Information("Browser context created successfully");
            Console.WriteLine("[Browser] Context created ✓");
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(60000);

            string excelFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MedalliaData.xlsx");

            Console.WriteLine("\n[Nuance] Navigating to Nuance Portal (SSO Login)...");
            await page.GotoAsync(NuanceSsoUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            Console.WriteLine("Opening Nuance SSO...");

            var usernameField = page.Locator("input[type='email'], input[type='text'][placeholder*='email'], input[name='loginfmt']").First;
            await usernameField.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30000 });
            await usernameField.FillAsync(loginId);

            var nextButton = page.GetByRole(AriaRole.Button, new() { Name = "Next" });
            await nextButton.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            await page.WaitForURLAsync(url => url.StartsWith("https://portal.xchat.xfinity.com/") && !url.Contains("/sso/"));
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            await page.GotoAsync(TranscriptSearchUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForURLAsync(url => url.Contains("transcriptSearchRT"), new() { Timeout = 30000 });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.Locator("#status").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60000 });

            var multipleEngagementButton = page.Locator("xpath=//*[@id='status']/button[2]");
            if (!await multipleEngagementButton.IsVisibleAsync())
            {
                throw new InvalidOperationException("Multiple Engagement Search button not found or not visible.");
            }
            await multipleEngagementButton.ClickAsync();
            await Task.Delay(2000);

            var textarea = page.Locator("xpath=//*[@id='main']/div[2]/query-builder-rt/div/div[2]/div/div[2]/div[2]/textarea");
            await textarea.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
            await textarea.FillAsync(engagementIds);

            await RemoveFilterAsync(page, "Agent Group Name", TopFilterRemoveButtonXPath, 5000, 800);
            await RemoveFilterAsync(page, "Business Unit Name", TopFilterRemoveButtonXPath, 5000, 1600);
            await RemoveFilterAsync(page, "Business Rule Name", TopFilterRemoveButtonXPath, 8000, 1600);
            await RemoveFilterAsync(page, "Sales Qualified", SalesQualifiedRemoveButtonXPath, 5000, 800);

            await EnterItemListValuesAsync(page, ItemListValues);

            var transcriptDownload = await ExportTranscriptsAsync(page, InstantExportOptionXPath, "/html/body/div[6]/div/div/div/form/div[2]/div[3]/div[1]/input");
            savedFiles.Add(await SaveDownloadAsync(transcriptDownload, "NuanceTranscripts"));

            var secondTranscriptDownload = await ExportTranscriptsAsync(page, SecondExportOptionXPath, "/html/body/div[6]/div/div/div/form/div[2]/div[2]/div[1]/input", "1");
            savedFiles.Add(await SaveDownloadAsync(secondTranscriptDownload, "NuanceTranscripts2"));

            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await context.StorageStateAsync();

            Console.WriteLine($"Nuance export completed. Saved {savedFiles.Count} file(s) to {ExportFolderPath}");
            return savedFiles;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Nuance] Download failed for engagement IDs ({engagementIds}): {ex.Message}");
            throw;
        }
        finally
        {
            await CloseBrowserSafelyAsync(browser);
            playwright?.Dispose();
        }
    }

    /// <summary>
    /// Closes the browser, ignoring the case where the browser or driver already exited.
    /// This prevents a TargetClosedException from hiding the original error.
    /// </summary>
    private static async Task CloseBrowserSafelyAsync(IBrowser? browser)
    {
        if (browser is null)
        {
            return;
        }

        try
        {
            await browser.CloseAsync();
        }
        catch (PlaywrightException)
        {
            Console.WriteLine("[Nuance] Browser was already closed; skipping close.");
        }
    }

    private static async Task RemoveFilterAsync(IPage page, string filterName, string removeButtonXPath, int waitMs, int delayMs)
    {
        Console.WriteLine($"[Cleanup] Removing filter: {filterName}");
        var removeButton = page.Locator($"xpath={removeButtonXPath}");
        if (await removeButton.IsVisibleAsync())
        {
            await removeButton.ClickAsync();
            await Task.Delay(delayMs);
        }
        else
        {
            Console.WriteLine($"Filter '{filterName}' not present; skipped");
        }
    }

    private static async Task EnterItemListValuesAsync(IPage page, IEnumerable<string> values)
    {
        var input = page.Locator($"xpath={ItemListInputXPath}");
        foreach (var value in values)
        {
            await input.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
            await input.ClickAsync();
            await input.FillAsync(value);

            var suggestion = page.Locator($"xpath={string.Format(ItemListSuggestionXPathFormat, value)}");
            await suggestion.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
            await suggestion.ClickAsync();
        }
    }

    private static async Task<IDownload> ExportTranscriptsAsync(IPage page, string exportOptionXPath, string quantityInputXPath, string? fixedQuantity = null)
    {
        var exportButton = page.Locator("id=addAttr-btn-keyboard-nav");
        await exportButton.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await exportButton.ClickAsync();
        await Task.Delay(2000);

        var exportOption = page.Locator($"xpath={exportOptionXPath}");
        await exportOption.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await exportOption.ClickAsync();
        await Task.Delay(2000);

        string batchCountStr;
        if (fixedQuantity != null)
        {
            batchCountStr = fixedQuantity;
        }
        else
        {
            var batchCountSpan = page.Locator("xpath=/html/body/div[6]/div/div/div/form/div[2]/div[3]/div[2]/span[1]");
            await batchCountSpan.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
            string batchCountText = await batchCountSpan.TextContentAsync() ?? string.Empty;
            batchCountStr = batchCountText.Trim().Split(' ')[1].Replace(".", string.Empty);
        }

        var quantityInput = page.Locator($"xpath={quantityInputXPath}");
        await quantityInput.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
        await quantityInput.FillAsync(batchCountStr);
        await Task.Delay(500);

        var okButton = page.Locator("xpath=/html/body/div[6]/div/div/div/form/div[3]/button[1]");
        await okButton.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });

        return await page.RunAndWaitForDownloadAsync(async () =>
        {
            await okButton.ClickAsync();
        }, new() { Timeout = 120000 });
    }

    private static async Task<string> SaveDownloadAsync(IDownload download, string filePrefix)
    {
        string extension = Path.GetExtension(download.SuggestedFilename);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".xlsx";
        }

        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
        string filePath = Path.Combine(ExportFolderPath, $"{filePrefix}_{timestamp}{extension}");

        await download.SaveAsAsync(filePath);

        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
        {
            throw new InvalidDataException($"Downloaded file is missing or empty: {filePath}");
        }

        Console.WriteLine($"Nuance file saved: {filePath}");
        return filePath;
    }
}
