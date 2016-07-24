IF OBJECT_ID('CustomerGrains', 'U') IS NOT NULL
DROP TABLE CustomerGrains
GO
IF OBJECT_ID('Upsert_CustomerGrains') IS NOT NULL
DROP PROCEDURE Upsert_CustomerGrains
GO
IF TYPE_ID ('GrainKeyListType') IS NOT NULL
DROP TYPE GrainKeyListType;
GO
IF TYPE_ID ('CustomerGrainsType') IS NOT NULL
DROP TYPE CustomerGrainsType;
GO


/*
*
* TABLES
*
*/
CREATE TABLE CustomerGrains(
	[GrainKey] [nvarchar](256) NOT NULL,
	[CustomerId] [int] NULL,
	[FirstName] [nvarchar](256) NULL,
	[LastName] [nvarchar](256) NULL,
	[NickName] [nvarchar](256) NULL,
	[BirthDate] [date] NULL,
	[Gender] [int] NULL,
	[Country] [nvarchar](256) NULL,
	[AvatarUrl] [nvarchar](256) NULL,
	[KudoPoints] [int] NULL,
	[Status] [int] NULL,
	[LastLogin] [datetime] NULL,
	[Devices] [nvarchar](2000) NULL
 CONSTRAINT [PK_CustomerGrains_GrainKey] PRIMARY KEY CLUSTERED 
 (
	[GrainKey] ASC
 )WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON)
)
GO


/*
* 
* TABLE TYPES
*
*/
CREATE TYPE GrainKeyListType AS TABLE(
	GrainKey nvarchar(256) NULL
)
GO
CREATE TYPE CustomerGrainsType AS TABLE(
	[GrainKey] [nvarchar](256) NULL,
	[CustomerId] [int] NULL,
	[FirstName] [nvarchar](256) NULL,
	[LastName] [nvarchar](256) NULL,
	[NickName] [nvarchar](256) NULL,
	[BirthDate] [date] NULL,
	[Gender] [int] NULL,
	[Country] [nvarchar](256) NULL,
	[AvatarUrl] [nvarchar](256) NULL,
	[KudoPoints] [int] NULL,
	[Status] [int] NULL,
	[LastLogin] [datetime] NULL,
	[Devices] [nvarchar](2000) NULL
)
GO


/*
*
* STORED PROCEDURES
*
*/
CREATE PROCEDURE Upsert_CustomerGrains
	@List CustomerGrainsType READONLY
AS 
BEGIN
	SET NOCOUNT ON;
	BEGIN TRANSACTION;

	-- Update  
	UPDATE G
	SET G.CustomerId = L.CustomerId, 
		G.FirstName = L.FirstName,
		G.LastName = L.LastName,
		G.NickName = L.NickName,
		G.BirthDate = L.BirthDate,
		G.Gender = L.Gender,
		G.Country = L.Country,
		G.AvatarUrl = L.AvatarUrl,
		G.KudoPoints = L.KudoPoints,
		G.Status = L.Status,
		G.LastLogin = L.LastLogin,
		G.Devices = L.Devices
	FROM CustomerGrains G INNER JOIN @List L
	ON L.GrainKey = G.GrainKey

	-- Insert Statement
	INSERT INTO CustomerGrains (GrainKey, CustomerId, FirstName, LastName,NickName, BirthDate, Gender, Country, AvatarUrl, KudoPoints, Status, LastLogin, Devices)
	SELECT GrainKey, CustomerId, FirstName, LastName, NickName, BirthDate, Gender, Country, AvatarUrl, KudoPoints, Status, LastLogin,  Devices
	FROM @List L
	WHERE NOT EXISTS (SELECT 1 
					  FROM CustomerGrains
					  WHERE GrainKey = L.GrainKey)

	
	COMMIT TRANSACTION 
END
GO