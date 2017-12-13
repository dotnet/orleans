CREATE TABLE OrleansStatisticsTable
(
    OrleansStatisticsTableId SERIAL NOT NULL ,
    DeploymentId varchar(150) NOT NULL,
    Timestamp timestamp(3) NOT NULL DEFAULT (now() at time zone 'utc'),
    Id varchar(250) NOT NULL,
    HostName varchar(150) NOT NULL,
    Name varchar(150) NOT NULL,
    IsValueDelta boolean NOT NULL,
    StatValue varchar(1024) NOT NULL,
    Statistic varchar(512) NOT NULL,

    CONSTRAINT StatisticsTable_StatisticsTableId PRIMARY KEY(OrleansStatisticsTableId)
);

CREATE TABLE OrleansClientMetricsTable
(
    DeploymentId varchar(150) NOT NULL,
    ClientId varchar(150) NOT NULL,
    Timestamp timestamp(3) NOT NULL DEFAULT (now() at time zone 'utc'),
    Address varchar(45) NOT NULL,
    HostName varchar(150) NOT NULL,
    CpuUsage float(53) NOT NULL,
    MemoryUsage bigint NOT NULL,
    SendQueueLength integer NOT NULL,
    ReceiveQueueLength integer NOT NULL,
    SentMessages bigint NOT NULL,
    ReceivedMessages bigint NOT NULL,
    ConnectedGatewayCount bigint NOT NULL,

    CONSTRAINT PK_ClientMetricsTable_DeploymentId_ClientId PRIMARY KEY (DeploymentId , ClientId)
);

CREATE TABLE OrleansSiloMetricsTable
(
    DeploymentId varchar(150) NOT NULL,
    SiloId varchar(150) NOT NULL,
    Timestamp timestamp(3) NOT NULL DEFAULT (now() at time zone 'utc'),
    Address varchar(45) NOT NULL,
    Port integer NOT NULL,
    Generation integer NOT NULL,
    HostName varchar(150) NOT NULL,
    GatewayAddress varchar(45) NOT NULL,
    GatewayPort integer NOT NULL,
    CpuUsage float(53) NOT NULL,
    MemoryUsage bigint NOT NULL,
    SendQueueLength integer NOT NULL,
    ReceiveQueueLength integer NOT NULL,
    SentMessages bigint NOT NULL,
    ReceivedMessages bigint NOT NULL,
    ActivationCount integer NOT NULL,
    RecentlyUsedActivationCount integer NOT NULL,
    RequestQueueLength bigint NOT NULL,
    IsOverloaded boolean NOT NULL,
    ClientCount bigint NOT NULL,

    CONSTRAINT PK_SiloMetricsTable_DeploymentId_SiloId PRIMARY KEY (DeploymentId , SiloId)
);

CREATE FUNCTION upsert_report_client_metrics(
    DeploymentIdArg             OrleansClientMetricsTable.DeploymentId%TYPE,
    ClientIdArg                 OrleansClientMetricsTable.ClientId%TYPE,
    AddressArg                  OrleansClientMetricsTable.Address%TYPE,
    HostNameArg                 OrleansClientMetricsTable.HostName%TYPE,
    CpuUsageArg                 OrleansClientMetricsTable.CpuUsage%TYPE,
    MemoryUsageArg              OrleansClientMetricsTable.MemoryUsage%TYPE,
    SendQueueLengthArg          OrleansClientMetricsTable.SendQueueLength%TYPE,
    ReceiveQueueLengthArg       OrleansClientMetricsTable.ReceiveQueueLength%TYPE,
    SentMessagesArg             OrleansClientMetricsTable.SentMessages%TYPE,
    ReceivedMessagesArg         OrleansClientMetricsTable.ReceivedMessages%TYPE,
    ConnectedGatewayCountArg    OrleansClientMetricsTable.ConnectedGatewayCount%TYPE
  )
  RETURNS void AS
$func$
BEGIN

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
    SELECT
        DeploymentIdArg,
        ClientIdArg,
        AddressArg,
        HostNameArg,
        CpuUsageArg,
        MemoryUsageArg,
        SendQueueLengthArg,
        ReceiveQueueLengthArg,
        SentMessagesArg,
        ReceivedMessagesArg,
        ConnectedGatewayCountArg
    ON CONFLICT (DeploymentId, ClientId)
        DO UPDATE SET
            Timestamp = (now() at time zone 'utc'),
            Address = AddressArg,
            HostName = HostNameArg,
            CpuUsage = CpuUsageArg,
            MemoryUsage = MemoryUsageArg,
            SendQueueLength = SendQueueLengthArg,
            ReceiveQueueLength = ReceiveQueueLengthArg,
            SentMessages = SentMessagesArg,
            ReceivedMessages = ReceivedMessagesArg,
            ConnectedGatewayCount = ConnectedGatewayCountArg;

END
$func$ LANGUAGE plpgsql;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'UpsertReportClientMetricsKey','
    SELECT * FROM upsert_report_client_metrics(
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
');

CREATE FUNCTION upsert_silo_metrics(
    DeploymentIdArg                 OrleansSiloMetricsTable.DeploymentId%TYPE,
    SiloIdArg                       OrleansSiloMetricsTable.SiloId%TYPE,
    AddressArg                      OrleansSiloMetricsTable.Address%TYPE,
    PortArg                         OrleansSiloMetricsTable.Port%TYPE,
    GenerationArg                   OrleansSiloMetricsTable.Generation%TYPE,
    HostNameArg                     OrleansSiloMetricsTable.HostName%TYPE,
    GatewayAddressArg               OrleansSiloMetricsTable.GatewayAddress%TYPE,
    GatewayPortArg                  OrleansSiloMetricsTable.GatewayPort%TYPE,
    CpuUsageArg                     OrleansSiloMetricsTable.CpuUsage%TYPE,
    MemoryUsageArg                  OrleansSiloMetricsTable.MemoryUsage%TYPE,
    ActivationCountArg              OrleansSiloMetricsTable.ActivationCount%TYPE,
    RecentlyUsedActivationCountArg  OrleansSiloMetricsTable.RecentlyUsedActivationCount%TYPE,
    SendQueueLengthArg              OrleansSiloMetricsTable.SendQueueLength%TYPE,
    ReceiveQueueLengthArg           OrleansSiloMetricsTable.ReceiveQueueLength%TYPE,
    RequestQueueLengthArg           OrleansSiloMetricsTable.RequestQueueLength%TYPE,
    SentMessagesArg                 OrleansSiloMetricsTable.SentMessages%TYPE,
    ReceivedMessagesArg             OrleansSiloMetricsTable.ReceivedMessages%TYPE,
    IsOverloadedArg                 OrleansSiloMetricsTable.IsOverloaded%TYPE,
    ClientCountArg                  OrleansSiloMetricsTable.ClientCount%TYPE
  )
  RETURNS void AS
$func$
BEGIN

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
    SELECT
        DeploymentIdArg,
        SiloIdArg,
        AddressArg,
        PortArg,
        GenerationArg,
        HostNameArg,
        GatewayAddressArg,
        GatewayPortArg,
        CpuUsageArg,
        MemoryUsageArg,
        SendQueueLengthArg,
        ReceiveQueueLengthArg,
        SentMessagesArg,
        ReceivedMessagesArg,
        ActivationCountArg,
        RecentlyUsedActivationCountArg,
        RequestQueueLengthArg,
        IsOverloadedArg,
        ClientCountArg
    ON CONFLICT (DeploymentId, SiloId)
        DO UPDATE SET
            Timestamp = (now() at time zone 'utc'),
            Address = AddressArg,
            Port = PortArg,
            Generation = GenerationArg,
            HostName = HostNameArg,
            GatewayAddress = GatewayAddressArg,
            GatewayPort = GatewayPortArg,
            CpuUsage = CpuUsageArg,
            MemoryUsage = MemoryUsageArg,
            ActivationCount = ActivationCountArg,
            RecentlyUsedActivationCount = RecentlyUsedActivationCountArg,
            SendQueueLength = SendQueueLengthArg,
            ReceiveQueueLength = ReceiveQueueLengthArg,
            RequestQueueLength = RequestQueueLengthArg,
            SentMessages = SentMessagesArg,
            ReceivedMessages = ReceivedMessagesArg,
            IsOverloaded = IsOverloadedArg,
            ClientCount = ClientCountArg;

END
$func$ LANGUAGE plpgsql;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'UpsertSiloMetricsKey','
    SELECT * FROM upsert_silo_metrics(
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
        @ActivationCount,
        @RecentlyUsedActivationCount,
        @SendQueueLength,
        @ReceiveQueueLength,
        @RequestQueueLength,
        @SentMessages,
        @ReceivedMessages,
        @IsOverloaded,
        @ClientCount
    )
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'InsertOrleansStatisticsKey','

    START TRANSACTION;
    INSERT INTO OrleansStatisticsTable
    (
        deploymentid,
        id,
        hostname,
        name,
        isvaluedelta,
        statvalue,
        statistic
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
