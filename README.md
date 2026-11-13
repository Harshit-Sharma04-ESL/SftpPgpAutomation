# SftpPgpAutomation

A .NET 9 console application that runs as a batch job. It downloads the latest set of PGP-encrypted files from an SFTP server, decrypts them, loads the decrypted CSV/Excel data into a SQL Server staging table, transfers the rows into a final table through a stored procedure, records each run in an execution log table, and sends a status email.

> **Note:** This README is based on the current source code. Items that are not implemented are marked "Not documented in the current codebase" or listed under Future Improvements.

---

## 1. Project Overview

### Project Name and Purpose
**SftpPgpAutomation** (`SftpPgpAutomation.csproj`, root namespace `SftpPgpAutomation`) automates the daily ingestion of AICR (interaction/quality review) data. The application is a single executable that is intended to be run on a schedule by an external scheduler (for example, Windows Task Scheduler). Scheduling is **not** implemented inside the application.

### Business Problem
Partner files arrive on an SFTP server as PGP-encrypted files named `nonAICRDetail_yyyy-MM-dd.csv.pgp`. Manually downloading, decrypting, and loading these files is error-prone and slow. This application:
- Pulls only files that have not already been loaded.
- Decrypts them with a PGP private key.
- Loads them into SQL Server with a repeatable, transactional process.
- Records run outcomes and notifies the team by email.

### Key Functionalities
- **SFTP download** of new `nonAICRDetail_*.csv.pgp` files, skipping any whose source file name already exists in the staging table (`SftpDownloader.DownloadFilesNotStaged`).
- **PGP decryption** of each `.pgp` file to a plain file in a shared output directory (`PgpDecryptor.DecryptFileAsync`).
- **CSV and Excel parsing** of the decrypted file. CSV uses `TextFieldParser`; Excel (`.xlsx`) uses ClosedXML and reads the first worksheet (`ExcelStagingService.ReadExcel`).
- **Staging load** into `dbo.AICR_Staging` using `SqlBulkCopy` inside a transaction. Existing rows for the same source file are deleted first, so reruns are idempotent per file.
- **Final load** through the stored procedure `dbo.usp_LoadAICRFinal`, which cleans, type-maps, de-duplicates, and inserts new rows into `dbo.AICR_Final`.
- **Retention cleanup**: files in the output directory older than 15 days (last-write time) are deleted (`FileProcessingService.DeleteExpiredOutputFiles`).
- **Execution logging** of each run to SQL Server (`LoggingService.LogExecution`), with the 5 most recent runs printed to the console.
- **Email notification** of success or failure with run metrics (`MailService.SendExecutionNotification`). Recipients are read from the stored procedure `dbo.usp_getDailerUploadEmail`.

---

## 2. Technology Stack

| Category | Technology / Package | Version (from `SftpPgpAutomation.csproj`) |
|---|---|---|
| Language | C# (implicit usings and nullable reference types enabled) | - |
| Runtime / Framework | .NET (`net9.0`), console application (`OutputType` = `Exe`) | .NET 9.0 |
| SFTP client | `SSH.NET` (`Renci.SshNet`) | 2026.0.0 |
| PGP | `PgpCore` | 8.1.1 |
| Excel parsing | `ClosedXML` | 0.104.2 |
| Database access | `Microsoft.Data.SqlClient` (raw ADO.NET; no ORM) | 5.2.0 |
| Configuration | `Microsoft.Extensions.Configuration.Binder`, `Microsoft.Extensions.Configuration.Json` | 9.0.0 |
| CSV parsing | `Microsoft.VisualBasic.FileIO.TextFieldParser` (part of the .NET SDK reference set) | - |
| Email | `System.Net.Mail` (`SmtpClient`) | - |

### Database and ORM
- **Microsoft SQL Server.** The connection string name is `ExecutionLogsDb`. The sample configuration points to database `dbauditPortal`.
- **ORM:** None. The code uses raw ADO.NET (`SqlConnection`, `SqlCommand`, `SqlBulkCopy`).

### External Integrations
- **SFTP server** (sample host configured: `xfer.comcast.com`, port `222`). Authentication uses an SSH private key in PuTTY `.ppk` format.
- **PGP** decryption using a private key file (`Keys\pgp-private-key.asc`) and passphrase.
- **SMTP server** for notification emails.
- **Network share** for decrypted output (UNC path configured in `DecryptedOutputDirectory`).

---

## 3. Project Architecture

### High-Level Architecture
This is a single-process, sequential pipeline. There are no web APIs, no hosted services, and no background worker threads. `Program.cs` builds the object graph manually (there is no DI container) and calls `ExecutionCoordinator.ExecuteAsync()` once.

```
Windows Task Scheduler (external)
			│
			▼
   Program.cs (composition root, appsettings.json)
			│
			▼
   ExecutionCoordinator  ──────────────┐
			│                          │
			▼                          ▼
   FileProcessingService          LoggingService ──► SQL Server (ExecutionLogs)
	 │          │                 MailService ──────► SMTP / SQL Server (email config)
	 │          ▼
	 │   ExcelStagingService ──► SQL Server (AICR_Staging, usp_LoadAICRFinal → AICR_Final)
	 ▼
   SftpDownloader (SFTP)  +  PgpDecryptor (PGP)
```

### Application Workflow
`ExecutionCoordinator.ExecuteAsync()` runs the following sequence:

1. **Ensure directories.** `ConfigurationService.EnsureDirectoriesExist()` creates `LocalDownloadDirectory` and `DecryptedOutputDirectory` if they are missing.
2. **Process files.** `FileProcessingService.ProcessFilesAsync()`:
   1. Deletes expired files in the output directory (older than 15 days).
   2. Reads distinct `SourceFileName` values from `dbo.AICR_Staging` (`ExcelStagingService.GetStagedSourceFileNames`).
   3. Lists the remote directory and downloads every `nonAICRDetail_yyyy-MM-dd.csv.pgp` file whose base name (without `.pgp`) is not already staged. Files already present locally are reused, not re-downloaded.
   4. For each downloaded file:
	  - Decrypts it to the output directory (`PgpDecryptor`). Existing output files are skipped.
	  - Stages and loads it (`ExcelStagingService.ProcessExcelFile`).
	  - Deletes the local encrypted copy.
   5. Failures are counted per file. Processing continues with the next file.
3. **Display summary.** `FileProcessingService.DisplaySummary` prints totals.
4. **Log execution.** `LoggingService.LogExecution` inserts a row into the execution log table, then prints the 5 most recent runs. Logging is skipped when `Logging:EnableExecutionLogging` is `false`.
5. **Send notification.** `MailService.SendExecutionNotification` sends the status email. This runs in the `finally` block, so it is attempted even after an error.
6. **Exit code.** `Environment.ExitCode` is `0` on success and `1` on an application-level exception.

### Component Responsibilities

| Component | File | Responsibility |
|---|---|---|
| Composition root | `Program.cs` | Loads `appsettings.json`, constructs services, sets exit code. |
| Coordinator | `Services/ExecutionCoordinator.cs` | Orchestrates the run, handles top-level errors, writes the execution log, triggers email. |
| File workflow | `Services/FileProcessingService.cs` | Retention cleanup, download-then-decrypt-then-stage loop, result aggregation. |
| Configuration | `Services/ConfigurationService.cs` | Binds `Sftp`, `Pgp`, `Logging` sections; validates required settings and key-file paths. |
| Staging/final load | `Services/ExcelStagingService.cs` | Parses CSV/Excel, creates the staging table, bulk-loads rows, calls `usp_LoadAICRFinal`. |
| Email | `Services/MailService.cs` | Builds an HTML email and sends it via SMTP. |
| Execution log | `LoggingService.cs` | Creates the log table if needed, inserts and reads run records, prints the log table. |
| SFTP | `SftpDownloader.cs` | Connects with SSH key auth, lists the remote folder, downloads new files. |
| PGP | `PgpDecryptor.cs` | Decrypts a `.pgp` file safely (temp file, then rename). |
| Models | `ExecutionLog.cs`, `Settings/ApplicationSettings.cs` | Execution-log DTO and strongly typed settings classes. |
| SQL scripts | `Database/*.sql` | Manual schema setup for the final table, procedure, and execution log. |

### Data Flow Between Components

```
SFTP server ──(SshNet)──► Local download folder (*.csv.pgp)
								  │ PgpDecryptor
								  ▼
					   Decrypted output folder (*.csv / *.xlsx, no .pgp)
								  │ ExcelStagingService.ReadExcel
								  ▼
					   dbo.AICR_Staging (NVARCHAR(4000) columns + SourceFileName, LoadedAt)
								  │ EXEC dbo.usp_LoadAICRFinal @SourceFileName
								  ▼
					   dbo.AICR_Final (typed, de-duplicated)

Run metrics ──► tbl_ExecutionLogs ──► console table
Run metrics ──► email (recipients from dbo.usp_getDailerUploadEmail)
```

---

## 4. Project Structure

```
SftpPgpAutomation/
├── Program.cs                         Entry point; builds configuration and services
├── SftpPgpAutomation.csproj           Project file (net9.0, package references, copy rules)
├── SftpPgpAutomation.slnx             Solution file
├── appsettings.json                   Runtime configuration (see Section 7)
├── ExecutionLog.cs                    Execution-log model
├── LoggingService.cs                  SQL Server execution-log persistence and display
├── PgpDecryptor.cs                    Static PGP decryption helper
├── SftpDownloader.cs                  Static SFTP listing and download helper
├── Settings/
│   └── ApplicationSettings.cs         SftpSettings, PgpSettings, LoggingSettings, ApplicationSettings
├── Services/
│   ├── ConfigurationService.cs        Settings binding and validation
│   ├── ExcelStagingService.cs         CSV/Excel reading, staging table load, final-table procedure call
│   ├── ExecutionCoordinator.cs        Top-level workflow and exit-code handling
│   ├── FileProcessingService.cs       File workflow and FileProcessingResult model
│   └── MailService.cs                 Notification email
├── Database/
│   ├── AICR_Staging_And_Final.sql     Creates dbo.AICR_Final and dbo.usp_LoadAICRFinal
│   └── ExecutionLogs_Schema.sql       Creates ExecutionLogs, indexes, and dbo.AICR_ACSR_InsertLog
├── Keys/
│   ├── pgp-private-key.asc            PGP private key (sensitive; copied to output)
│   └── ssh-private-key.ppk            SSH private key in PuTTY format (sensitive; copied to output)
├── .gitignore
└── .gitattributes
```

### Major Classes and Their Responsibilities

**`Program` (top-level statements in `Program.cs`)**
- Builds `IConfiguration` from `appsettings.json` in `AppContext.BaseDirectory`.
- Creates `ConfigurationService`, `ExcelStagingService`, `FileProcessingService`, `LoggingService`, `MailService`, and `ExecutionCoordinator`.
- Sets `Environment.ExitCode`.

**`ExecutionCoordinator`**
- `ExecuteAsync()`: runs the workflow, returns `0` or `1`.
- `LogExecutionAsync(...)`: builds an `ExecutionLog` and calls `LoggingService`. Logging failures are printed and do not change the exit code.
- `DetermineStatus(...)`: returns `Failed`, `Success`, or `PartialSuccess`.
- `DisplayExecutionLogs()`: prints the last 5 runs.

**`ConfigurationService`**
- Binds the `Sftp`, `Pgp`, and `Logging` sections to `ApplicationSettings`.
- Reads `ConnectionStrings:ExecutionLogsDb`.
- `ValidateSettings()`: throws `InvalidOperationException` listing all missing values and missing key files.
- `EnsureDirectoriesExist()`: creates local download and output directories.
- `GetSshPassphrase()`: reads environment variable `SSH_KEY_PASSPHRASE`. **Not used anywhere in the current code.**

**`FileProcessingService`**
- `ProcessFilesAsync()`: main file workflow (see Section 3).
- `DeleteExpiredOutputFiles()`: deletes files older than `RetentionDays` (15).
- `DecryptFilesAsync(...)`: per-file decrypt, stage, and delete loop with per-file error isolation.
- `DisplaySummary(...)`: prints the console summary.
- `FileProcessingResult`: counts (`TotalFilesFound`, `SuccessfullyDecrypted`, `FailureCount`), `FailedFiles`, and the `IsSuccessful` / `IsPartialSuccess` flags.

**`ExcelStagingService`**
- `ProcessExcelFile(string)`: reads the file, adds `SourceFileName`, creates or extends the staging table, deletes prior rows for the same file, bulk-copies rows, commits, then runs `dbo.usp_LoadAICRFinal`.
- `GetStagedSourceFileNames()`: returns distinct `SourceFileName` values from `dbo.AICR_Staging`.
- `ReadExcel(...)` / `ReadCsv(...)`: parsing. Every cell is stored as text. Empty values become `DBNull`.
- `EnsureStagingTable(...)`: creates `dbo.AICR_Staging` from the file headers, with each column as `NVARCHAR(4000) NULL`. Note that the method's summary says it "adds any new columns", but the current code only creates the table when it is missing.

**`SftpDownloader` (static)**
- `DownloadFilesNotStaged(...)`: the method used by the workflow. It lists the remote directory, filters by regex `^nonAICRDetail_\d{4}-\d{2}-\d{2}\.csv\.pgp$` (case-insensitive), skips staged names, downloads the rest, and returns local paths.
- `DownloadLatestFile(...)`: downloads only the latest-dated matching file. **Not called by the current workflow.**

**`PgpDecryptor` (static)**
- `DecryptFileAsync(encryptedPath, privateKeyPath, passphrase, outputDirectory)`: validates inputs, skips if output exists, decrypts to a `.<guid>.tmp` file, moves it into place, and deletes the temp file in `finally`.

**`LoggingService`**
- Constructor: reads `ConnectionStrings:ExecutionLogsDb` and runs `InitializeDatabase()`.
- `LogExecution(ExecutionLog)`: inserts a run record.
- `GetAllLogs()`, `GetRecentLogs(int count = 10)`: read records.
- `DisplayLogsTable(...)`: prints a formatted table.

**`MailService`**
- `SendExecutionNotification(...)`: no-op when `Mail:Enabled` is `false`. Otherwise it loads recipients from the database, builds an HTML body, and sends via SMTP.
- `LoadMailRecipients()`: executes `dbo.usp_getDailerUploadEmail` and expects three columns: From, To, and Cc.

**`ExecutionLog`**: DTO with `Id`, timing fields, file counts, `Status`, `ErrorMessage`, directories, and a computed `SuccessRate`.

---

## 5. Prerequisites

### Required SDKs and Runtimes
- **.NET 9 SDK** (for building). Install from https://dotnet.microsoft.com/download/dotnet/9.0.
- **.NET 9 Runtime** (for running the built output on a target host).
- **Windows** is assumed. Paths in the default configuration are Windows paths, and the decrypted output directory is a UNC share.

### Development Tools
- **Visual Studio 2022 17.12+ or Visual Studio 2026** with the ".NET desktop development" or ".NET multi-platform App UI" workload, or **VS Code** with the C# Dev Kit.
- **Git** for cloning.

### Database Requirements
- **Microsoft SQL Server** reachable from the execution host.
- A database (sample name `dbauditPortal`) with these objects:
  - `dbo.AICR_Final` table and `dbo.usp_LoadAICRFinal` procedure (run `Database/AICR_Staging_And_Final.sql`).
  - `dbo.AICR_Staging` table. The application creates this automatically.
  - Execution log table `tbl_ExecutionLogs`. The application creates this automatically (see Section 9 for a known mismatch).
  - `dbo.usp_getDailerUploadEmail` stored procedure. **Not documented in the current codebase.** Its definition is not in this repository. It must return three columns: `From`, `To`, and `Cc`.
- A SQL login with permissions to create tables, insert, bulk-copy, and execute the procedures above.

### External Dependencies
- **SFTP server** with the expected remote directory and files.
- **SSH private key** in `.ppk` format that the SFTP account accepts.
- **PGP private key** (ASCII-armored `.asc`) matching the public key used by the sender, plus its passphrase.
- **SMTP relay** reachable from the execution host on the configured port (no authentication is configured in code).
- **Network access** to the decrypted output UNC path (for example, `\\arlmssan01\listtech$\...`).

---

## 6. Installation and Setup

### 6.1 Clone the Repository
```powershell
git clone https://github.com/Harshit-Sharma04-ESL/SftpPgpAutomation.git
cd SftpPgpAutomation
```

### 6.2 Restore Dependencies
```powershell
dotnet restore SftpPgpAutomation.csproj
```

### 6.3 Configure Application Settings
1. Copy the sample `appsettings.json` and replace every placeholder and secret with values for your environment. Do not commit real values (see Section 7 and Section 13).
2. Place the SSH private key (`.ppk`) and PGP private key (`.asc`) under `Keys\` or update the paths in configuration.

### 6.4 Set Up Database Requirements
1. Create or choose the target database (sample: `dbauditPortal`).
2. Run `Database/AICR_Staging_And_Final.sql` once against that database. It creates `dbo.AICR_Final` and `dbo.usp_LoadAICRFinal`.
3. Run `Database/ExecutionLogs_Schema.sql` if you want the `ExecutionLogs` and `dbo.AICR_ACSR_InsertLog` objects it defines. See Section 9 for how these relate to `tbl_ExecutionLogs`.
4. Create `dbo.usp_getDailerUploadEmail` (not documented in the current codebase).
5. Ensure the login in `ExecutionLogsDb` can create tables, indexes, and execute these objects.

### 6.5 Build and Run the Application
```powershell
dotnet build SftpPgpAutomation.csproj -c Release
dotnet run --project SftpPgpAutomation.csproj
```
The build copies `appsettings.json` and the `Keys\` files to the output folder (`bin\Release\net9.0\`).

---

## 7. Configuration

### 7.1 Configuration Files
| File | Purpose | Copied to output |
|---|---|---|
| `appsettings.json` | All runtime settings (SFTP, PGP, logging, mail, connection string) | Yes (`PreserveNewest`) |
| `Keys\ssh-private-key.ppk` | SSH private key for SFTP login | Yes (`PreserveNewest`) |
| `Keys\pgp-private-key.asc` | PGP private key for decryption | Yes (`PreserveNewest`) |

`Program.cs` loads `appsettings.json` with `reloadOnChange: false`. The file is required (`optional: false`). Environment variable overrides are **not** wired up in `Program.cs`. Only `AddJsonFile` is registered.

### 7.2 Settings Reference

#### `Sftp`
| Key | Type | Description | Example (placeholder) |
|---|---|---|---|
| `Host` | string | SFTP host name | `<sftp-host>` |
| `Port` | int | SFTP port (must be > 0) | `<port>` |
| `Username` | string | SFTP user name | `<sftp-username>` |
| `RemoteDirectory` | string | Remote folder to list | `/OUTGOING/OPERATIONS` |
| `ExpectedHostFingerprintSHA256` | string | Intended host key fingerprint. **Bound but not used**; see Section 13. | `<sha256-fingerprint>` |
| `SshPrivateKeyPath` | string | Path to the `.ppk` key (must exist) | `Keys\ssh-private-key.ppk` |
| `SshPrivateKeyPassphrase` | string | **Bound but not used**; the SFTP client is created without a passphrase. | `<leave-empty-or-omit>` |
| `LocalDownloadDirectory` | string | Folder for downloaded encrypted files (created if missing) | `C:\SftpPgpAutomation\downloads` |
| `DecryptedOutputDirectory` | string | Folder for decrypted files, also used for retention cleanup (created if missing) | `\\<server>\<share>\<folder>` |

#### `Pgp`
| Key | Type | Description | Example (placeholder) |
|---|---|---|---|
| `PrivateKeyPath` | string | Path to the ASCII-armored PGP private key (must exist) | `Keys\pgp-private-key.asc` |
| `Passphrase` | string | Passphrase for the PGP private key (required) | `<pgp-passphrase>` |

#### `Logging`
| Key | Type | Default | Description |
|---|---|---|---|
| `EnableExecutionLogging` | bool | `true` | When `false`, the run is not written to the database. |

#### `Mail`
| Key | Type | Default | Description |
|---|---|---|---|
| `Enabled` | bool | `true` | When `false`, no email is sent. |
| `SmtpServer` | string | - | SMTP host. Required when mail is enabled. |
| `Port` | int | `25` | SMTP port. |
| `ErrorTo` | string | - | Recipient(s) for failure emails. Separate multiple addresses with `,` or `;`. Falls back to the stored-procedure `To` value if empty. |
| `SuccessSubject` | string | `SFTP PGP Automation` | Subject prefix for success emails. |
| `FailureSubject` | string | `SFTP PGP Automation` | Subject prefix for failure emails. |

> Note: `MailService` reads `Mail:SuccessSubject` and `Mail:FailureSubject` as subject prefixes. The subject becomes `<prefix> - <Success|Failed> - <dd/MM/yyyy HH:mm>`.

#### `ConnectionStrings`
| Key | Description |
|---|---|
| `ExecutionLogsDb` | SQL Server connection string. Required. Used for the execution log, staging, final load, and email recipient lookup. |

### 7.3 Example `appsettings.json` (placeholders only)
```json
{
  "Sftp": {
	"Host": "<sftp-host>",
	"Port": 222,
	"Username": "<sftp-username>",
	"RemoteDirectory": "/OUTGOING/OPERATIONS",
	"ExpectedHostFingerprintSHA256": "<sha256-host-key-fingerprint>",
	"SshPrivateKeyPath": "Keys\\ssh-private-key.ppk",
	"SshPrivateKeyPassphrase": "",
	"LocalDownloadDirectory": "C:\\SftpPgpAutomation\\downloads",
	"DecryptedOutputDirectory": "\\\\<server>\\<share>\\<folder>"
  },
  "Pgp": {
	"PrivateKeyPath": "Keys\\pgp-private-key.asc",
	"Passphrase": "<pgp-passphrase>"
  },
  "Logging": {
	"EnableExecutionLogging": true
  },
  "Mail": {
	"Enabled": true,
	"SmtpServer": "<smtp-host>",
	"Port": 25,
	"ErrorTo": "<error-recipient@example.com>",
	"SuccessSubject": "<success-subject>",
	"FailureSubject": "<failure-subject>"
  },
  "ConnectionStrings": {
	"ExecutionLogsDb": "Server=<server>,<port>;Database=<database>;User Id=<user>;Password=<password>;TrustServerCertificate=True;"
  }
}
```

### 7.4 Environment Variables
| Variable | Used by | Status |
|---|---|---|
| `SSH_KEY_PASSPHRASE` | `ConfigurationService.GetSshPassphrase()` | Defined but **not called** anywhere in the current code. |

Not documented in the current codebase: any other environment variables. The configuration builder does not read environment variables.

### 7.5 Secrets and Placeholders
Do not store real credentials, connection strings, passphrases, or private keys in source control. The repository's `appsettings.json` and `Keys\` folder currently contain sensitive values. Rotate them and move them to a secure store (see Section 13).

---

## 8. Execution and Usage

### 8.1 Run from Visual Studio
1. Open `SftpPgpAutomation.slnx`.
2. Set `SftpPgpAutomation` as the startup project.
3. Press **F5** (debug) or **Ctrl+F5** (run without debugging).

### 8.2 Run from the Command Line
```powershell
# Build
dotnet build SftpPgpAutomation.csproj -c Release

# Run the built executable (run from the output folder so appsettings.json and Keys\ are found)
cd .\bin\Release\net9.0
.\SftpPgpAutomation.exe

# Or run via dotnet
dotnet run --project SftpPgpAutomation.csproj -c Release
```

Because `Program.cs` loads `appsettings.json` from `AppContext.BaseDirectory`, the key paths in configuration that are relative (for example `Keys\ssh-private-key.ppk`) are resolved relative to the **current working directory**, not the base directory. Run from the output folder or use absolute paths. This is an important detail for scheduled execution.

### 8.3 Exit Codes
| Code | Meaning |
|---|---|
| `0` | Workflow completed without an unhandled exception. This includes runs where some files failed to decrypt. Check the summary and email. |
| `1` | An application-level exception occurred (for example, configuration validation failed or the SFTP connection failed). |

### 8.4 Scheduling
Scheduling is **not implemented** in the application. Use an external scheduler such as Windows Task Scheduler to run the executable on a daily schedule. Set the working directory to the output folder.

### 8.5 Important Workflows

**Normal daily run**
1. Expired output files are removed.
2. Staged source file names are read from `dbo.AICR_Staging`.
3. New `nonAICRDetail_*.csv.pgp` files are downloaded.
4. Each file is decrypted, staged, transferred to `dbo.AICR_Final`, and its encrypted local copy is deleted.
5. A run record is written, the summary is printed, and the email is sent.

**Re-running after a failure**
- A file whose source name is already in `dbo.AICR_Staging` is not downloaded again.
- If a decrypted output file already exists, `PgpDecryptor` skips decryption and returns without error. Staging still runs for that file in `FileProcessingService`. This is because the decrypted file path is reused.
- Staging deletes and reloads rows for the same `SourceFileName`, so a partial load can be safely re-run.

**Sample console output (structure only)**
```
============================================================
SFTP DOWNLOAD AND PGP DECRYPTION
============================================================

Connecting to SFTP server...
Connected to SFTP server successfully.
Downloaded successfully: C:\SftpPgpAutomation\downloads\nonAICRDetail_2025-01-15.csv.pgp
Decrypting: nonAICRDetail_2025-01-15.csv.pgp
  ✓ Successfully decrypted
Staged 1234 record(s) from nonAICRDetail_2025-01-15.csv.
Transferred staged records to AICR_Final for nonAICRDetail_2025-01-15.csv.

PROCESSING SUMMARY
...
```

### 8.6 Useful Queries
```sql
-- Recent runs (table used by the application)
SELECT TOP 10 Id, ExecutionStartTime, ExecutionEndTime, TotalFilesDownloaded,
	   TotalFilesDecrypted, DecryptionFailures, Status
FROM tbl_ExecutionLogs
ORDER BY ExecutionStartTime DESC;

-- Source files already loaded
SELECT DISTINCT SourceFileName FROM dbo.AICR_Staging;
```

---

## 9. Database Documentation

### 9.1 Database
- **Name (sample):** `dbauditPortal`
- **Engine:** Microsoft SQL Server
- **Access:** Raw ADO.NET via `Microsoft.Data.SqlClient`

### 9.2 Tables

| Object | Created by | Purpose |
|---|---|---|
| `dbo.AICR_Staging` | Application (`ExcelStagingService.EnsureStagingTable`) | Raw load target. Columns come from the file header row, each `NVARCHAR(4000) NULL`. Plus `StagingId` (identity PK), `SourceFileName NVARCHAR(260) NOT NULL`, and `LoadedAt DATETIME2 DEFAULT GETUTCDATE()`. |
| `dbo.AICR_Final` | `Database/AICR_Staging_And_Final.sql` | Typed final table. Columns: `Id`, `AICRDate`, `InteractionMedium`, `Vendor`, `AgentName`, `AccountNumber`, `InteractionStartDateTime1`, `HandleTime`, `CHAT_ACTIVITY_ID`, `VIOLATING_UNIVERSAL_CALL_ID`, `VIOLATING_CHAT_ACTIVITY_ID`, `Location`, `NTLOGIN_ID`, `LoadedAt`, `agentAICRStatus`. Unique constraint `UQ_AICR_Final_Natural` on (`AccountNumber`, `InteractionStartDateTime1`, `CHAT_ACTIVITY_ID`). |
| `tbl_ExecutionLogs` | Application (`LoggingService.InitializeDatabase`) | Run history: start and end time, file counts, duration in milliseconds, `Status` (`Success`, `PartialSuccess`, `Failed`), error message, directories, `CreatedAt`. |
| `ExecutionLogs` | `Database/ExecutionLogs_Schema.sql` | Same design as `tbl_ExecutionLogs`, with a different name. See the mismatch note in 9.5. |
| `dbo.AICR_ACSR_InsertLog` | `Database/ExecutionLogs_Schema.sql` | Per-row insert log table (`RunId`, `SourceId`, `AccountNumber`, `InsertStatus`, and so on). **Not referenced by any code in this repository.** |
| `dbo.usp_getDailerUploadEmail` | **Not documented in the current codebase** | Must return three columns (From, To, Cc) for mail. |

### 9.3 Stored Procedures

**`dbo.usp_LoadAICRFinal` (`@SourceFileName NVARCHAR(260)`)**
Defined in `Database/AICR_Staging_And_Final.sql`. It transfers rows for one source file from `dbo.AICR_Staging` to `dbo.AICR_Final`:
1. Cleans values with `LTRIM(RTRIM(...))` and `NULLIF(..., '')`.
2. Converts `AICRDate` and `InteractionStartDateTime1` with `ISDATE` and `CONVERT`. Invalid values become `NULL`.
3. Converts `HandleTime` to `INT` when it contains only digits and is 1 to 9 characters long.
4. Keeps rows where `AccountNumber`, `InteractionStartDateTime1`, and `CHAT_ACTIVITY_ID` are present.
5. Removes duplicates within the file with `ROW_NUMBER()` over the natural key.
6. Inserts only rows that do not already exist in `AICR_Final` for the natural key.
7. Runs in a transaction with `TRY/CATCH`. The error is re-raised with `RAISERROR`.

**`dbo.usp_getDailerUploadEmail`**: called by `MailService`. Not documented in the current codebase.

### 9.4 Relationships and Data Flow
```
Decrypted CSV/XLSX
   │  (SqlBulkCopy, delete-then-insert per SourceFileName)
   ▼
dbo.AICR_Staging  (all text columns)
   │  EXEC dbo.usp_LoadAICRFinal @SourceFileName
   ▼
dbo.AICR_Final    (typed, de-duplicated by natural key)

ExecutionCoordinator ──► tbl_ExecutionLogs
```
There are no foreign keys between these tables. `AICR_Staging` rows are linked to `AICR_Final` only by the `SourceFileName` parameter and the business key values.

### 9.5 Required Scripts and Initial Setup
1. **`Database/AICR_Staging_And_Final.sql`**: run once. It creates `dbo.AICR_Final` and `dbo.usp_LoadAICRFinal`.
2. **`Database/ExecutionLogs_Schema.sql`**: run once if you want the schema objects it defines.
3. The application creates `dbo.AICR_Staging` and `tbl_ExecutionLogs` on first use. Those statements are in code, not in the scripts.

**Known issues in the current scripts and code** (verify before relying on them):
- `LoggingService.InitializeDatabase()` checks for a table named `ExecutionLogs` but creates `tbl_ExecutionLogs`. If `ExecutionLogs` exists (from `ExecutionLogs_Schema.sql`) and `tbl_ExecutionLogs` does not, the check is false and no table is created. If `ExecutionLogs` does not exist and `tbl_ExecutionLogs` already exists, the next run tries to create `tbl_ExecutionLogs` again and fails. Align the table name in one place.
- `AICR_Staging_And_Final.sql` checks `OBJECT_ID(N'dbo.cc', N'P')` before `DROP PROCEDURE dbo.usp_LoadAICRFinal`. The drop never runs, so re-running the script fails with an "already exists" error on `CREATE PROCEDURE`. Change the check to `dbo.usp_LoadAICRFinal`.
- `ExecutionLogs_Schema.sql` creates `CREATE INDEX` statements without an `IF NOT EXISTS` guard. Re-running it fails.

---

## 10. Logging and Error Handling

### 10.1 Logging Implementation
- **Console logging only.** The application writes to `Console.WriteLine` (progress and summary) and `Console.Error.WriteLine` (errors). No logging framework (Serilog, NLog, `ILogger`) is used.
- **Execution log in SQL Server.** `LoggingService` inserts one row per run into `tbl_ExecutionLogs`. Enable or disable with `Logging:EnableExecutionLogging`.
- **Email notification.** `MailService` sends a success or failure email for every run, including runs that failed with an exception.

### 10.2 Log Locations
- **Console output:** the terminal, or the scheduler's captured output. Task Scheduler does not capture stdout unless you redirect it. Example:
  ```powershell
  .\SftpPgpAutomation.exe *>> "C:\SftpPgpAutomation\logs\run.log"
  ```
  Not documented in the current codebase: a built-in log file.
- **Execution history:** `tbl_ExecutionLogs` in the database specified in `ConnectionStrings:ExecutionLogsDb`.

### 10.3 Error Handling Behavior
- **Configuration errors:** `ConfigurationService` throws `InvalidOperationException` listing all invalid values. The exception is caught by `ExecutionCoordinator`, and the exit code is `1`.
- **Per-file errors:** `FileProcessingService.DecryptFilesAsync` catches exceptions per file. The file is counted in `FailureCount` and added to `FailedFiles`. The loop continues.
- **Decryption:** `PgpDecryptor` writes to a temp file and only moves it into place on success. The temp file is deleted in `finally`. Existing output files are skipped.
- **Staging:** `ExcelStagingService.ProcessExcelFile` stages in one transaction and runs the procedure after commit. If the procedure fails, the staged rows remain, and the file is **not** deleted locally (because the exception is caught and counted as a failure). A rerun reloads the rows.
- **Logging failures:** Errors in `LogExecutionAsync` are printed and do not change the exit code.
- **Mail failures:** Errors in `SendExecutionNotification` are printed and swallowed.

### 10.4 Common Errors and Troubleshooting

| Symptom (console message) | Likely cause | Action |
|---|---|---|
| `Configuration validation failed:` followed by a list | Required keys are empty, or key files do not exist at the configured paths. | Fix `appsettings.json`. Check paths are relative to the **current working directory**. |
| `Connection string 'ExecutionLogsDb' not found in configuration` | Missing `ConnectionStrings` entry. | Add the key. |
| `SSH private key not found.` | `SshPrivateKeyPath` is wrong, or the working directory differs from the expected path. | Use an absolute path or run from the output folder. |
| `No matching files found.` / `No new encrypted files available for decryption.` | No `nonAICRDetail_yyyy-MM-dd.csv.pgp` files are new on the server. | Confirm the remote folder and file names. Staged files are skipped by design. |
| SSH connection exceptions (`Renci.SshNet`) | Wrong host, port, user, or key. Host unreachable. Server rejects the key. | Test the connection with an SFTP client. Verify the key format (`.ppk` is expected) and that the account has access. |
| `Decryption failed: ...` | Wrong PGP passphrase or wrong private key. The file may be corrupted. | Verify `Pgp:Passphrase` and key pairing. Re-download the file. |
| `Input file must have a .pgp extension.` | The downloaded file name does not end in `.pgp`. | Confirm the remote file name pattern. |
| `Excel header row contains empty column names` / `CSV header row contains empty column names` | The first row has blank headers. | Fix the source file. |
| `Excel file has no data` / `CSV file has no data` | The file is empty. | Check the upstream file. |
| `usp_getDailerUploadEmail returned no email configuration` | The procedure returned no rows, or it is missing. Logged as an email error. | Create or fix the procedure. |
| `Mail:SmtpServer is not configured` / `Error sending notification email` | SMTP host missing, or the relay refuses connection. | Check SMTP host, port, and relay access. |
| `Error logging execution:` | The database is unreachable, or the table schema is wrong (see Section 9.5). | Verify the connection string and table name. |
| SQL error from `usp_LoadAICRFinal` | Data issue, or the procedure is missing. | Verify the procedure exists. Check the rows in `dbo.AICR_Staging` for the file. |
| `Warning: Could not display logs:` | Log table query failed. | Check the database connection and the table name. |

---

## 11. Deployment

### 11.1 Build and Publish
```powershell
# Framework-dependent publish (requires .NET 9 runtime on the target)
dotnet publish SftpPgpAutomation.csproj -c Release -o .\publish

# Self-contained publish for a specific Windows runtime (no runtime needed on the target)
dotnet publish SftpPgpAutomation.csproj -c Release -r win-x64 --self-contained true -o .\publish-selfcontained
```
Copy the contents of the publish folder to the target host. Then:
1. Add `appsettings.json` with environment-specific values (do not use the repository copy).
2. Place the SSH and PGP key files in the `Keys\` folder next to the executable, or update the paths to absolute locations.
3. Verify the target host can reach the SFTP server, SMTP relay, SQL Server, and the decrypted output share.

### 11.2 Environment-Specific Considerations
- **Paths:** Configuration uses Windows paths and a UNC share. Adjust for each environment.
- **Working directory:** Relative key paths depend on the working directory. Set the scheduled task's "Start in" folder to the application folder.
- **Database:** Use a separate database or schema per environment if needed. The scripts assume `dbauditPortal`.
- **Mail:** Set `Mail:Enabled` to `false` in non-production environments if you do not want emails sent.
- **Logging:** Set `Logging:EnableExecutionLogging` to `false` only if you do not need run history.

### 11.3 Deployment Prerequisites
- .NET 9 runtime (framework-dependent) or a self-contained publish.
- Service account with:
  - Read access to the SSH and PGP key files.
  - Write access to `LocalDownloadDirectory` and `DecryptedOutputDirectory`.
  - Database permissions described in Section 5.
  - Network access to SFTP, SMTP, and SQL Server.
- Scheduler (for example, Windows Task Scheduler) configured to run the executable on the required schedule.

### 11.4 Not documented in the current codebase
- CI/CD pipeline definitions.
- Containerization (Docker) files.
- Scheduler configuration.

---

## 12. Troubleshooting

| Issue | Possible causes | Recommended solutions |
|---|---|---|
| App exits immediately with code `1` | Configuration validation failed. | Read the console output for the listed validation messages. Correct `appsettings.json`. |
| `Configuration validation failed: SSH private key not found at: ...` | Relative path resolved against a different working directory. | Use an absolute path, or run from the output folder. |
| Nothing is downloaded, but files exist on the server | Staged names match, or names do not match the regex `^nonAICRDetail_\d{4}-\d{2}-\d{2}\.csv\.pgp$`. | Query `SELECT DISTINCT SourceFileName FROM dbo.AICR_Staging`. Check file names on the server. |
| Repeated `Decryption failed` | Wrong passphrase, wrong key, or a corrupted download. | Confirm the key pair. Remove the local `.pgp` file and rerun. |
| Output file exists but data is missing in `AICR_Final` | The procedure rejects rows (for example, invalid `InteractionStartDateTime1`, or duplicate natural keys). | Query `dbo.AICR_Staging` for the file. Check the `WHERE` conditions in `usp_LoadAICRFinal`. |
| Execution log errors on every run | `tbl_ExecutionLogs` missing or a schema mismatch. | See Section 9.5. Create the table manually with the script shape from `LoggingService`. |
| Email not received | `Mail:Enabled` is `false`, `usp_getDailerUploadEmail` returns no rows, SMTP relay blocked. | Check each setting. The error appears in the console output. |
| Old output files disappear | Retention cleanup removes files older than 15 days by last-write time. | Keep copies of files that must be retained longer. |
| Re-running the procedure script fails | Script issue (see Section 9.5). | Edit the script as described, or run only the `CREATE` statements you need. |

---

## 13. Security Considerations

### 13.1 Credential Management
- **Current state:** `appsettings.json` in the repository contains live-looking values for the SFTP username, PGP passphrase, SMTP host, and a SQL Server connection string with a password. **Treat these as compromised. Rotate them, remove them from source control history, and move them to a secure store.**
- **Recommended:**
  - Store secrets in environment variables, Windows Credential Manager, Azure Key Vault, or another secret store. Note that the current code does not read environment variables into configuration (Section 7.4). Add `AddEnvironmentVariables()` or a secret provider in `Program.cs` if needed.
  - Use Windows integrated security for SQL Server where possible instead of a SQL password.
  - Restrict read access to `Keys\` and `appsettings.json` to the service account.

### 13.2 Sensitive Data Handling
- **Private keys** (`Keys\pgp-private-key.asc`, `Keys\ssh-private-key.ppk`) are copied to the output folder by the project file. Restrict permissions on the output folder. Do not commit keys to source control.
- **Decrypted files** are written to `DecryptedOutputDirectory` as plain text. They contain business data. Restrict share permissions. Retention cleanup deletes files older than 15 days.
- **Local encrypted downloads** are deleted after successful decryption and staging (`File.Delete(file)` in `FileProcessingService`). Files that fail are left in `LocalDownloadDirectory`.
- **Email content** includes file names, counts, the output directory path, and error messages. Do not send sensitive data in error text.

### 13.3 Encryption and Authentication
- **PGP:** Files are decrypted with `PgpCore` using the configured private key and passphrase.
- **SFTP authentication:** Private key authentication via SSH.NET. `SshPrivateKeyPassphrase` and `ExpectedHostFingerprintSHA256` are **bound from configuration but not used**. The SFTP client does not verify the server host key against the configured fingerprint. Add a host key verification check (for example, `HostKeyReceived` in SSH.NET) before production use.
- **SMTP:** No authentication or TLS is configured in `MailService`. It uses the relay as configured.
- **SQL Server:** The connection string may use SQL authentication. `TrustServerCertificate=True` disables certificate validation. Use a trusted certificate in production.

### 13.4 Input Handling
- SQL statements use parameters for values. The staging table and column names are built from file headers (`[{c.ColumnName}]` in `EnsureStagingTable`). Header values from untrusted files can therefore influence SQL identifiers. Validate headers against an allow-list before production use.
- The `ExecutionLogs` insert and select statements are parameterized.

---

## 14. Future Improvements

These are gaps identified from the code. They are not existing functionality.

1. **Secrets management.** Move passwords and passphrases out of `appsettings.json`. Add environment variable or secret-store configuration.
2. **SFTP host key verification.** Use the configured `ExpectedHostFingerprintSHA256` to validate the server key. It is currently unused.
3. **Unused SSH passphrase.** Either use `SshPrivateKeyPassphrase` or `GetSshPassphrase()`, or remove them.
4. **Unused download method.** `SftpDownloader.DownloadLatestFile` is not called. Remove it or document its purpose.
5. **Table name mismatch.** Align `ExecutionLogs` and `tbl_ExecutionLogs` (Section 9.5).
6. **Procedure script check.** Fix the `OBJECT_ID` name in `AICR_Staging_And_Final.sql` so re-running works.
7. **Idempotent schema scripts.** Add `IF NOT EXISTS` guards to indexes in `ExecutionLogs_Schema.sql`.
8. **Structured logging.** Replace `Console` output with `Microsoft.Extensions.Logging` and write to a file or the event log.
9. **Working-directory independence.** Resolve relative key and output paths against `AppContext.BaseDirectory`.
10. **Automated tests.** No test project exists in the repository.
11. **Dependency injection and hosting.** The composition root is manual. Using `Microsoft.Extensions.Hosting` would simplify testing and configuration.
12. **Staging refresh behavior.** `EnsureStagingTable` does not add new columns to an existing table, although its summary says it does. Either implement this or correct the summary.
13. **Procedure error handling.** A failed `usp_LoadAICRFinal` leaves rows in the staging table and the local encrypted file in place. Consider explicit retry or quarantine logic.
