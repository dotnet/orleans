CREATE TABLE OrleansStatisticsTable
(
	OrleansStatisticsTableId INT IDENTITY(1,1) NOT NULL,
	DeploymentId NVARCHAR(150) NOT NULL,
	Timestamp DATETIME2(3) NOT NULL DEFAULT GETUTCDATE(),
	Id NVARCHAR(250) NOT NULL,
	HostName NVARCHAR(150) NOT NULL,
	Name NVARCHAR(150) NOT NULL,
	IsValueDelta BIT NOT NULL,
	StatValue NVARCHAR(1024) NOT NULL,
	Statistic NVARCHAR(512) NOT NULL,

	CONSTRAINT StatisticsTable_StatisticsTableId PRIMARY KEY(OrleansStatisticsTableId)
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'InsertOrleansStatisticsKey','
	BEGIN TRANSACTION;
	INSERT INTO OrleansStatisticsTable
	(
		DeploymentId,
		Id,
		HostName,
		Name,
		IsValueDelta,
		StatValue,
		Statistic
	)
	SELECT
		@DeploymentId,
		@Id,
		@HostName,
		@Name,
		@IsValueDelta,
		@StatValue,
		@Statistic;
	COMMIT TRANSACTION;
');
