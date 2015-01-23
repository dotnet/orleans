DROP TABLE [dbo].[OrleansMembershipTable]
DROP TABLE [dbo].[OrleansMembershipVersionTable]
DROP TABLE [dbo].[OrleansRemindersTable]
DROP TABLE [dbo].[OrleansSiloMetricsTable]
DROP TABLE [dbo].[OrleansClientMetricsTable]
DROP TABLE [dbo].[OrleansStatisticsTable]

CREATE TABLE [dbo].[OrleansMembershipTable]
(
	[DeploymentId] NVARCHAR(150) NOT NULL, 
    [Address] NVARCHAR(150) NOT NULL, 
    [Port] INT NOT NULL, 
    [Generation] INT NOT NULL, 
    [HostName] NVARCHAR(150) NOT NULL, 
    [Status] INT NOT NULL, 
    [ProxyPort] INT NULL, 
    [Primary] BIT NULL, 
    [RoleName] NVARCHAR(150) NULL, 
    [InstanceName] NVARCHAR(150) NULL, 
    [UpdateZone] INT NULL, 
    [FaultZone] INT NULL, 
    [SuspectingSilos] NTEXT NULL, 
    [SuspectingTimes] NTEXT NULL, 
    [StartTime] DATETIME NOT NULL, 
    [IAmAliveTime] DATETIME NOT NULL, 
	[ETag] NVARCHAR(50) NOT NULL,
    PRIMARY KEY ([DeploymentId],[Address],[Port],[Generation])
)

CREATE TABLE [dbo].[OrleansMembershipVersionTable]
(
	[DeploymentId] NVARCHAR(150) NOT NULL, 
    [TimeStamp] DATETIME NOT NULL, 
    [Version] BIGINT NOT NULL, 
	[ETag] NVARCHAR(50) NOT NULL,
    PRIMARY KEY ([DeploymentId])
)

CREATE TABLE [dbo].[OrleansRemindersTable]
(
	[ServiceId] NVARCHAR(150) NOT NULL, 
    [GrainId] NVARCHAR(150) NOT NULL, 
	[ReminderName] NVARCHAR(150) NOT NULL,
    [StartTime] DATETIME NOT NULL, 
    [Period] INT NOT NULL,
	[GrainIdConsistentHash] INT NOT NULL, 
	[ETag] NVARCHAR(50) NOT NULL,
    PRIMARY KEY ([ServiceId],[GrainId],[ReminderName])
)

CREATE TABLE [dbo].[OrleansSiloMetricsTable]
(
	[DeploymentId] NVARCHAR(150) NOT NULL, 
    [SiloId] NVARCHAR(150) NOT NULL, 
    [TimeStamp] DATETIME NOT NULL, 
    [Address] NVARCHAR(50) NOT NULL, 
    [Port] INT NOT NULL, 
    [Generation] INT NOT NULL, 
    [HostName] NVARCHAR(150) NOT NULL, 
    [GatewayAddress] NVARCHAR(50) NULL, 
    [GatewayPort] INT NULL, 
	[CPU] FLOAT NOT NULL,
	[Memory] BIGINT NOT NULL,
    [Activations] INT NOT NULL, 
    [SendQueue] INT NOT NULL, 
    [ReceiveQueue] INT NOT NULL, 
	[RequestQueue] BIGINT NOT NULL,
	[SentMessages] BIGINT NOT NULL,
	[ReceivedMessages] BIGINT NOT NULL,
	[LoadShedding] BIT NOT NULL,
	[ClientCount] BIGINT NOT NULL,
    PRIMARY KEY ([DeploymentId],[SiloId])
)

CREATE TABLE [dbo].[OrleansClientMetricsTable]
(
	[DeploymentId] NVARCHAR(150) NOT NULL, 
    [ClientId] NVARCHAR(150) NOT NULL, 
    [TimeStamp] DATETIME NOT NULL, 
    [Address] NVARCHAR(50) NOT NULL, 
    [HostName] NVARCHAR(150) NOT NULL, 
	[CPU] FLOAT NOT NULL,
	[Memory] BIGINT NOT NULL,
    [SendQueue] INT NOT NULL, 
    [ReceiveQueue] INT NOT NULL, 
	[SentMessages] BIGINT NOT NULL,
	[ReceivedMessages] BIGINT NOT NULL,
	[ConnectedGWCount] BIGINT NOT NULL,
    PRIMARY KEY ([DeploymentId],[ClientId])
)

CREATE TABLE [dbo].[OrleansStatisticsTable]
(
	[DeploymentId] NVARCHAR(150) NOT NULL, 
    [Date] DATE NOT NULL, 
    [TimeStamp] DATETIME NOT NULL, 
    [Id] NVARCHAR(250) NOT NULL, 
    [Counter] BIGINT NOT NULL, 
    [HostName] NVARCHAR(150) NOT NULL, 
    [Name] NVARCHAR(150) NULL, 
    [IsDelta] BIT NULL, 
	[StatValue] NVARCHAR(250) NOT NULL,
	[Statistic] NVARCHAR(250) NOT NULL,
    PRIMARY KEY ([DeploymentId],[Date],[Id],[Counter])
)

SELECT * FROM [dbo].[OrleansMembershipTable]
SELECT * FROM [dbo].[OrleansMembershipVersionTable]
SELECT * FROM [dbo].[OrleansRemindersTable]
SELECT * FROM [dbo].[OrleansSiloMetricsTable]
SELECT * FROM [dbo].[OrleansClientMetricsTable]
SELECT * FROM [dbo].[OrleansStatisticsTable] ORDER BY TimeStamp
