using System.Data;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;
using Microsoft.VisualBasic.FileIO;

namespace SftpPgpAutomation.Services;

/// <summary>
/// Loads every row of a decrypted Excel file into the staging table, then transfers
/// the required columns into the final table through a stored procedure.
/// </summary>
public class ExcelStagingService
{
    private const string StagingTable = "dbo.AICR_Staging";
    private const string LoadProcedure = "dbo.usp_LoadAICRFinal";

    private readonly string _connectionString;

    public ExcelStagingService(ConfigurationService configService)
    {
        _connectionString = configService.ConnectionString;
    }

    /// <summary>
    /// Stages all records from the Excel file and then runs the final-table transfer.
    /// Staging and transfer are committed separately, so the final table is only touched
    /// after every staged record has been saved.
    /// </summary>
    public int ProcessExcelFile(string excelFilePath)
    {
        var sourceFileName = Path.GetFileName(excelFilePath);
        var dataTable = ReadExcel(excelFilePath, sourceFileName);
        if (!dataTable.Columns.Contains("SourceFileName"))
        {
            dataTable.Columns.Add("SourceFileName", typeof(string)).SetOrdinal(0);
        }
        foreach (DataRow row in dataTable.Rows)
        {
            row["SourceFileName"] = sourceFileName;
        }

        using var connection = new SqlConnection(_connectionString);
        connection.Open();

        using (var transaction = connection.BeginTransaction())
        {
            EnsureStagingTable(connection, transaction, dataTable);

            using (var clear = new SqlCommand($"DELETE FROM {StagingTable} WHERE SourceFileName = @fileName", connection, transaction))
            {
                clear.Parameters.Add("@fileName", SqlDbType.NVarChar, 260).Value = sourceFileName;
                clear.ExecuteNonQuery();
            }

            using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
            {
                bulkCopy.DestinationTableName = StagingTable;
                bulkCopy.BulkCopyTimeout = 0;
                foreach (DataColumn column in dataTable.Columns)
                {
                    bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                }
                bulkCopy.WriteToServer(dataTable);
            }

            transaction.Commit();
        }

        Console.WriteLine($"Staged {dataTable.Rows.Count} record(s) from {sourceFileName}.");

        using var loadCommand = new SqlCommand(LoadProcedure, connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 0
        };
        loadCommand.Parameters.Add("@SourceFileName", SqlDbType.NVarChar, 260).Value = sourceFileName;
        loadCommand.ExecuteNonQuery();

        Console.WriteLine($"Transferred staged records to AICR_Final for {sourceFileName}.");
        return dataTable.Rows.Count;
    }

    /// <summary>
    /// Returns the distinct source file names already present in the staging table.
    /// </summary>
    public HashSet<string> GetStagedSourceFileNames()
    {
        var stagedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var connection = new SqlConnection(_connectionString);
        connection.Open();

        using var command = new SqlCommand(
            $"IF OBJECT_ID(N'{StagingTable}', N'U') IS NOT NULL SELECT DISTINCT SourceFileName FROM {StagingTable};",
            connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            stagedFileNames.Add(reader.GetString(0));
        }

        return stagedFileNames;
    }

    /// <summary>
    /// Reads the file into a DataTable. CSV files are parsed directly; Excel files use the first worksheet.
    /// Each header becomes a staging column; every cell is stored as text.
    /// </summary>
    private static DataTable ReadExcel(string excelFilePath, string sourceFileName)
    {
        if (Path.GetExtension(excelFilePath).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return ReadCsv(excelFilePath, sourceFileName);
        }

        using var workbook = new XLWorkbook(excelFilePath);
        var worksheet = workbook.Worksheets.First();
        var range = worksheet.RangeUsed()
            ?? throw new InvalidOperationException($"Excel file has no data: {sourceFileName}");

        var table = new DataTable();
        var headers = range.FirstRow().Cells().Select(c => c.GetString().Trim()).ToList();

        if (headers.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"Excel header row contains empty column names: {sourceFileName}");

        foreach (var header in headers)
        {
            table.Columns.Add(header, typeof(string));
        }

        foreach (var row in range.RowsUsed().Skip(1))
        {
            var values = new object?[headers.Count];
            for (var i = 0; i < headers.Count; i++)
            {
                var cell = row.Cell(i + 1);
                var text = cell.GetFormattedString();
                values[i] = string.IsNullOrWhiteSpace(text) ? DBNull.Value : text;
            }
            table.Rows.Add(values);
        }

        return table;
    }

    /// <summary>
    /// Parses a CSV file. The first line is the header row; every field is stored as text.
    /// </summary>
    private static DataTable ReadCsv(string csvFilePath, string sourceFileName)
    {
        using var parser = new TextFieldParser(csvFilePath)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true
        };
        parser.SetDelimiters(",");

        if (parser.EndOfData)
            throw new InvalidOperationException($"CSV file has no data: {sourceFileName}");

        var headers = (parser.ReadFields() ?? Array.Empty<string>()).Select(h => h.Trim()).ToList();
        if (headers.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"CSV header row contains empty column names: {sourceFileName}");

        var table = new DataTable();
        foreach (var header in headers)
        {
            table.Columns.Add(header, typeof(string));
        }

        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields() ?? Array.Empty<string>();
            var values = new object?[headers.Count];
            for (var i = 0; i < headers.Count; i++)
            {
                var text = i < fields.Length ? fields[i].Trim() : string.Empty;
                values[i] = string.IsNullOrWhiteSpace(text) ? DBNull.Value : text;
            }
            table.Rows.Add(values);
        }

        return table;
    }

    /// <summary>
    /// Creates the staging table from the Excel headers if it does not exist, and adds any new columns.
    /// </summary>
    private static void EnsureStagingTable(SqlConnection connection, SqlTransaction transaction, DataTable dataTable)
    {
        var columnDefinitions = string.Join(", ", dataTable.Columns.Cast<DataColumn>()
            .Where(c => !c.ColumnName.Equals("SourceFileName", StringComparison.OrdinalIgnoreCase))
            .Select(c => $"[{c.ColumnName}] NVARCHAR(4000) NULL"));

        var sql = $@"
            IF OBJECT_ID(N'{StagingTable}', N'U') IS NULL
            BEGIN
                CREATE TABLE {StagingTable} (
                    StagingId INT IDENTITY(1,1) PRIMARY KEY,
                    SourceFileName NVARCHAR(260) NOT NULL,
                    LoadedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                    {columnDefinitions}
                );
            END";

        using var command = new SqlCommand(sql, connection, transaction);
        command.ExecuteNonQuery();
    }
}
