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
