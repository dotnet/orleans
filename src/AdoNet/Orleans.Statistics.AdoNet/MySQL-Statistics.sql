CREATE TABLE OrleansStatisticsTable
(
    OrleansStatisticsTableId INT NOT NULL AUTO_INCREMENT,
    DeploymentId NVARCHAR(150) NOT NULL,
    Timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Id NVARCHAR(250) NOT NULL,
    HostName NVARCHAR(150) NOT NULL,
    Name NVARCHAR(150) NOT NULL,
    IsValueDelta BIT NOT NULL,
    StatValue NVARCHAR(1024) NOT NULL,
    Statistic NVARCHAR(512) NOT NULL,

    CONSTRAINT StatisticsTable_StatisticsTableId PRIMARY KEY(OrleansStatisticsTableId)
);

CREATE TABLE OrleansClientMetricsTable
(
    DeploymentId NVARCHAR(150) NOT NULL,
    ClientId NVARCHAR(150) NOT NULL,
    Timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Address VARCHAR(45) NOT NULL,
    HostName NVARCHAR(150) NOT NULL,
    CpuUsage FLOAT NOT NULL,
    MemoryUsage BIGINT NOT NULL,
    SendQueueLength INT NOT NULL,
    ReceiveQueueLength INT NOT NULL,
    SentMessages BIGINT NOT NULL,
    ReceivedMessages BIGINT NOT NULL,
    ConnectedGatewayCount BIGINT NOT NULL,

    CONSTRAINT PK_ClientMetricsTable_DeploymentId_ClientId PRIMARY KEY (DeploymentId , ClientId)
);

CREATE TABLE OrleansSiloMetricsTable
(
    DeploymentId NVARCHAR(150) NOT NULL,
    SiloId NVARCHAR(150) NOT NULL,
    Timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Address VARCHAR(45) NOT NULL,
    Port INT NOT NULL,
    Generation INT NOT NULL,
    HostName NVARCHAR(150) NOT NULL,
    GatewayAddress VARCHAR(45) NOT NULL,
    GatewayPort INT NOT NULL,
    CpuUsage FLOAT NOT NULL,
    MemoryUsage BIGINT NOT NULL,
    SendQueueLength INT NOT NULL,
    ReceiveQueueLength INT NOT NULL,
    SentMessages BIGINT NOT NULL,
    ReceivedMessages BIGINT NOT NULL,
    ActivationCount INT NOT NULL,
    RecentlyUsedActivationCount INT NOT NULL,
    RequestQueueLength BIGINT NOT NULL,
    IsOverloaded BIT NOT NULL,
    ClientCount BIGINT NOT NULL,

    CONSTRAINT PK_SiloMetricsTable_DeploymentId_SiloId PRIMARY KEY (DeploymentId , SiloId)
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'UpsertReportClientMetricsKey','
    INSERT INTO OrleansClientMetricsTable
    (
        DeploymentId,
        ClientId,
        Address,
        HostName,
        CpuUsage,
        MemoryUsage,
        SendQueueLength,
        ReceiveQueueLength,
        SentMessages,
        ReceivedMessages,
        ConnectedGatewayCount
    )
    VALUES
    (
        @DeploymentId,
        @ClientId,
        @Address,
        @HostName,
        @CpuUsage,
        @MemoryUsage,
        @SendQueueLength,
        @ReceiveQueueLength,
        @SentMessages,
        @ReceivedMessages,
        @ConnectedGatewayCount
    )
    ON DUPLICATE KEY
    UPDATE
        Address = @Address,
        HostName = @HostName,
        CpuUsage = @CpuUsage,
        MemoryUsage = @MemoryUsage,
        SendQueueLength = @SendQueueLength,
        ReceiveQueueLength = @ReceiveQueueLength,
        SentMessages = @SentMessages,
        ReceivedMessages = @ReceivedMessages,
        ConnectedGatewayCount = @ConnectedGatewayCount;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'UpsertSiloMetricsKey','
    INSERT INTO OrleansSiloMetricsTable
    (
        DeploymentId,
        SiloId,
        Address,
        Port,
        Generation,
        HostName,
        GatewayAddress,
        GatewayPort,
        CpuUsage,
        MemoryUsage,
        SendQueueLength,
        ReceiveQueueLength,
        SentMessages,
        ReceivedMessages,
        ActivationCount,
        RecentlyUsedActivationCount,
        RequestQueueLength,
        IsOverloaded,
        ClientCount
    )
    VALUES
    (
        @DeploymentId,
        @SiloId,
        @Address,
        @Port,
        @Generation,
        @HostName,
        @GatewayAddress,
        @GatewayPort,
        @CpuUsage,
        @MemoryUsage,
        @SendQueueLength,
        @ReceiveQueueLength,
        @SentMessages,
        @ReceivedMessages,
        @ActivationCount,
        @RecentlyUsedActivationCount,
        @RequestQueueLength,
        @IsOverloaded,
        @ClientCount
    )
    ON DUPLICATE KEY
    UPDATE
        Address = @Address,
        Port = @Port,
        Generation = @Generation,
        HostName = @HostName,
        GatewayAddress = @GatewayAddress,
        GatewayPort= @GatewayPort,
        CpuUsage = @CpuUsage,
        MemoryUsage = @MemoryUsage,
        SendQueueLength = @SendQueueLength,
        ReceiveQueueLength = @ReceiveQueueLength,
        SentMessages = @SentMessages,
        ReceivedMessages = @ReceivedMessages,
        ActivationCount = @ActivationCount,
        RecentlyUsedActivationCount = @RecentlyUsedActivationCount,
        RequestQueueLength = @RequestQueueLength,
        IsOverloaded = @IsOverloaded,
        ClientCount = @ClientCount;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'InsertOrleansStatisticsKey','
    START TRANSACTION;
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
    COMMIT;
');
