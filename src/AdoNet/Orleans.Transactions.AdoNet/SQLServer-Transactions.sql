-- Orleans Transaction Key Table for SQL Server
CREATE TABLE OrleansTransactionKeyTable (
  StateId NVARCHAR(255) NOT NULL,
  ETag NVARCHAR(255) NOT NULL,
  CommittedSequenceId BIGINT NOT NULL,
  Metadata NVARCHAR(MAX) NULL,
  Timestamp DATETIME2 NOT NULL,
  PRIMARY KEY (StateId)
);

-- Orleans Transaction State Table for SQL Server
CREATE TABLE OrleansTransactionStateTable (
  StateId NVARCHAR(255) NOT NULL,
  SequenceId BIGINT NOT NULL,
  ETag NVARCHAR(255) NOT NULL,
  TransactionId NVARCHAR(255) NOT NULL,
  TransactionTimestamp DATETIME2 NOT NULL,
  TransactionManager NVARCHAR(MAX) NOT NULL,
  StateJson NVARCHAR(MAX) NULL,
  Timestamp DATETIME2 NOT NULL,
  PRIMARY KEY (StateId, SequenceId)
);

CREATE INDEX IX_State_Seq ON OrleansTransactionStateTable (StateId, SequenceId);
