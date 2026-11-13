/*
	AICR final table and transfer stored procedure.
	The staging table dbo.AICR_Staging is created by the application from the Excel header row
	(all columns NVARCHAR(4000), plus SourceFileName and LoadedAt).
	Run this script once against the dbauditPortal database.

	Uses only ISDATE/LIKE/CASE for type mapping so it works when the database
	compatibility level does not support TRY_CONVERT or TRY_CAST.
*/

IF OBJECT_ID(N'dbo.AICR_Final', N'U') IS NULL
BEGIN
	CREATE TABLE dbo.AICR_Final (
		Id INT IDENTITY(1,1) PRIMARY KEY,
		AICRDate DATE NULL,
		InteractionMedium NVARCHAR(200) NULL,
		Vendor NVARCHAR(200) NULL,
		AgentName NVARCHAR(200) NULL,
		AccountNumber NVARCHAR(100) NOT NULL,
		InteractionStartDateTime1 DATETIME2 NOT NULL,
		HandleTime INT NULL,
		CHAT_ACTIVITY_ID NVARCHAR(100) NOT NULL,
		VIOLATING_UNIVERSAL_CALL_ID NVARCHAR(100) NULL,
		VIOLATING_CHAT_ACTIVITY_ID NVARCHAR(100) NULL,
		Location NVARCHAR(200) NULL,
		NTLOGIN_ID NVARCHAR(100) NULL,
		LoadedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
		agentAICRStatus NVARCHAR(200) NULL,
		CONSTRAINT UQ_AICR_Final_Natural UNIQUE (AccountNumber, InteractionStartDateTime1, CHAT_ACTIVITY_ID)
	);
END;
GO

IF OBJECT_ID(N'dbo.cc', N'P') IS NOT NULL
	DROP PROCEDURE dbo.usp_LoadAICRFinal;
GO

CREATE PROCEDURE dbo.usp_LoadAICRFinal
	@SourceFileName NVARCHAR(260)
AS
BEGIN
	SET NOCOUNT ON;
	SET XACT_ABORT ON;

	BEGIN TRANSACTION;
	BEGIN TRY
		;WITH Clean AS (
			SELECT
				CASE WHEN ISDATE(LTRIM(RTRIM(s.AICRDate))) = 1
					 THEN CONVERT(DATE, LTRIM(RTRIM(s.AICRDate))) END AS AICRDate,
				NULLIF(LTRIM(RTRIM(s.InteractionMedium)), N'') AS InteractionMedium,
				NULLIF(LTRIM(RTRIM(s.Vendor)), N'') AS Vendor,
				NULLIF(LTRIM(RTRIM(s.AgentName)), N'') AS AgentName,
				LTRIM(RTRIM(s.AccountNumber)) AS AccountNumber,
				CASE WHEN ISDATE(LTRIM(RTRIM(s.InteractionStartDateTime1))) = 1
					 THEN CONVERT(DATETIME2, LTRIM(RTRIM(s.InteractionStartDateTime1))) END AS InteractionStartDateTime1,
				CASE WHEN LTRIM(RTRIM(s.HandleTime)) NOT LIKE N'%[^0-9]%'
					  AND LEN(LTRIM(RTRIM(s.HandleTime))) BETWEEN 1 AND 9
					 THEN CAST(LTRIM(RTRIM(s.HandleTime)) AS INT) END AS HandleTime,
				LTRIM(RTRIM(s.CHAT_ACTIVITY_ID)) AS CHAT_ACTIVITY_ID,
				NULLIF(LTRIM(RTRIM(s.VIOLATING_UNIVERSAL_CALL_ID)), N'') AS VIOLATING_UNIVERSAL_CALL_ID,
				NULLIF(LTRIM(RTRIM(s.VIOLATING_CHAT_ACTIVITY_ID)), N'') AS VIOLATING_CHAT_ACTIVITY_ID,
				NULLIF(LTRIM(RTRIM(s.Location)), N'') AS Location,
				NULLIF(LTRIM(RTRIM(s.NTLOGIN_ID)), N'') AS NTLOGIN_ID
			FROM dbo.AICR_Staging AS s
			WHERE s.SourceFileName = @SourceFileName
		),
		Valid AS (
			SELECT *,
				   ROW_NUMBER() OVER (
					   PARTITION BY AccountNumber, InteractionStartDateTime1, CHAT_ACTIVITY_ID
					   ORDER BY AICRDate
				   ) AS RowNum
			FROM Clean
			WHERE NULLIF(AccountNumber, N'') IS NOT NULL
			  AND InteractionStartDateTime1 IS NOT NULL
			  AND NULLIF(CHAT_ACTIVITY_ID, N'') IS NOT NULL
		)
		INSERT INTO dbo.AICR_Final (
			AICRDate,
			InteractionMedium,
			Vendor,
			AgentName,
			AccountNumber,
			InteractionStartDateTime1,
			HandleTime,
			CHAT_ACTIVITY_ID,
			VIOLATING_UNIVERSAL_CALL_ID,
			VIOLATING_CHAT_ACTIVITY_ID,
			Location,
			NTLOGIN_ID
		)
		SELECT
			v.AICRDate,
			v.InteractionMedium,
			v.Vendor,
			v.AgentName,
			v.AccountNumber,
			v.InteractionStartDateTime1,
			v.HandleTime,
			v.CHAT_ACTIVITY_ID,
			v.VIOLATING_UNIVERSAL_CALL_ID,
			v.VIOLATING_CHAT_ACTIVITY_ID,
			v.Location,
			v.NTLOGIN_ID
		FROM Valid AS v
		WHERE v.RowNum = 1
		  AND NOT EXISTS (
			  SELECT 1
			  FROM dbo.AICR_Final AS f
			  WHERE f.AccountNumber = v.AccountNumber
				AND f.InteractionStartDateTime1 = v.InteractionStartDateTime1
				AND f.CHAT_ACTIVITY_ID = v.CHAT_ACTIVITY_ID
		  );

		COMMIT TRANSACTION;
	END TRY
	BEGIN CATCH
		IF @@TRANCOUNT > 0
			ROLLBACK TRANSACTION;
		DECLARE @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE();
		RAISERROR(@ErrorMessage, 16, 1);
	END CATCH;
END;
GO
