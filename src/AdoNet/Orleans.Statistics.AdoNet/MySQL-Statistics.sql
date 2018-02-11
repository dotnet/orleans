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
