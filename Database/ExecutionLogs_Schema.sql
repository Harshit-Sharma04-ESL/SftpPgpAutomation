-- =====================================================
-- SFTP PGP Automation - Execution Logs Database Schema
-- Database: Microsoft SQL Server
-- Purpose: Track and audit all execution runs
-- =====================================================

-- Drop table if exists (for dev/test environments)
-- DROP TABLE IF EXISTS ExecutionLogs;

-- Create ExecutionLogs Table
CREATE TABLE ExecutionLogs (
	Id INT PRIMARY KEY IDENTITY(1,1),

	-- Execution Timing
	ExecutionStartTime DATETIME2 NOT NULL,
	ExecutionEndTime DATETIME2 NOT NULL,

	-- File Metrics
	TotalFilesDownloaded INT NOT NULL DEFAULT 0,
	TotalFilesDecrypted INT NOT NULL DEFAULT 0,
	DecryptionFailures INT NOT NULL DEFAULT 0,

	-- Execution Duration (in milliseconds)
	ExecutionDurationMilliseconds BIGINT NOT NULL,

	-- Status Tracking
	Status NVARCHAR(50) NOT NULL CHECK(Status IN ('Success', 'PartialSuccess', 'Failed')),
	ErrorMessage NVARCHAR(MAX),

	-- Directory Information
	RemoteDirectory NVARCHAR(500),
	OutputDirectory NVARCHAR(500),

	-- Audit Trail
	CreatedAt DATETIME2 DEFAULT GETUTCDATE()
);

-- Create Indexes for Better Performance
CREATE INDEX IX_ExecutionLogs_ExecutionStartTime 
	ON ExecutionLogs(ExecutionStartTime DESC);

CREATE INDEX IX_ExecutionLogs_Status 
	ON ExecutionLogs(Status);

CREATE INDEX IX_ExecutionLogs_CreatedAt 
	ON ExecutionLogs(CreatedAt DESC);

	IF OBJECT_ID(N'dbo.AICR_ACSR_InsertLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AICR_ACSR_InsertLog
    (
        LogId BIGINT IDENTITY(1,1) PRIMARY KEY,
        RunId UNIQUEIDENTIFIER NOT NULL,
        SourceId INT NULL,
        AccountNumber NVARCHAR(100) NULL,
        InteractionStartDateTime1 DATETIME2 NULL,
        CHAT_ACTIVITY_ID NVARCHAR(100) NULL,
        InsertStatus NVARCHAR(50) NOT NULL,
        ErrorMessage NVARCHAR(MAX) NULL,
        LoggedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
    );
END;
GO

-- =====================================================
-- Sample Queries
-- =====================================================

-- View all execution logs
-- SELECT * FROM ExecutionLogs ORDER BY ExecutionStartTime DESC;

-- Get recent executions (last 10)
-- SELECT TOP 10 Id, ExecutionStartTime, ExecutionEndTime, TotalFilesDownloaded, 
--        TotalFilesDecrypted, DecryptionFailures, Status
-- FROM ExecutionLogs 
-- ORDER BY ExecutionStartTime DESC;

-- Get failed executions
-- SELECT * FROM ExecutionLogs WHERE Status = 'Failed' ORDER BY ExecutionStartTime DESC;

-- Get execution summary of last 7 days
-- SELECT CAST(ExecutionStartTime AS DATE) as ExecutionDate,
--        COUNT(*) as TotalRuns,
--        SUM(TotalFilesDownloaded) as TotalDownloaded,
--        SUM(TotalFilesDecrypted) as TotalDecrypted,
--        SUM(DecryptionFailures) as TotalFailures,
--        AVG(CAST(ExecutionDurationMilliseconds AS FLOAT)) as AvgDuration
-- FROM ExecutionLogs
-- WHERE ExecutionStartTime >= DATEADD(DAY, -7, GETUTCDATE())
-- GROUP BY CAST(ExecutionStartTime AS DATE)
-- ORDER BY ExecutionStartTime DESC;

-- Get execution statistics
-- SELECT Status,
--        COUNT(*) as Count,
--        AVG(CAST(ExecutionDurationMilliseconds AS FLOAT)) as AvgDurationMs,
--        AVG(CAST(TotalFilesDownloaded AS FLOAT)) as AvgFilesDownloaded,
--        AVG(CAST(TotalFilesDecrypted AS FLOAT)) as AvgFilesDecrypted,
--        MAX(ExecutionStartTime) as LastExecution
-- FROM ExecutionLogs
-- GROUP BY Status;
