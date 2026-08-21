-- Orleans Transaction Key Table for SQL Server
CREATE TABLE OrleansTransactionKeyTable (
  StateId NVARCHAR(255) COLLATE Latin1_General_100_BIN2 NOT NULL,
  ETag NVARCHAR(36) NOT NULL,
  CommittedSequenceId BIGINT NOT NULL,
  Metadata VARBINARY(MAX) NULL,
  Timestamp DATETIME2(3) NOT NULL DEFAULT GETUTCDATE(),
  PRIMARY KEY (StateId)
);

-- Orleans Transaction State Table for SQL Server
CREATE TABLE OrleansTransactionStateTable (
  StateId NVARCHAR(255) COLLATE Latin1_General_100_BIN2 NOT NULL,
  SequenceId BIGINT NOT NULL,
  ETag NVARCHAR(36) NOT NULL,
  TransactionId NVARCHAR(36) NOT NULL,
  TransactionTimestampTicks BIGINT NOT NULL,
  TransactionManager VARBINARY(MAX) NOT NULL,
  StateData VARBINARY(MAX) NULL,
  Timestamp DATETIME2(3) NOT NULL DEFAULT GETUTCDATE(),
  PRIMARY KEY (StateId, SequenceId)
);

CREATE INDEX IX_State_Seq ON OrleansTransactionStateTable (StateId, SequenceId);
