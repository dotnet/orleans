-- Orleans Transaction Key Table for PostgreSQL
CREATE TABLE OrleansTransactionKeyTable (
  StateId VARCHAR(255) NOT NULL,
  ETag VARCHAR(255) NOT NULL,
  CommittedSequenceId BIGINT NOT NULL,
  Metadata TEXT NULL,
  Timestamp TIMESTAMP NOT NULL,
  PRIMARY KEY (StateId)
);

-- Orleans Transaction State Table for PostgreSQL
CREATE TABLE OrleansTransactionStateTable (
  StateId VARCHAR(255) NOT NULL,
  SequenceId BIGINT NOT NULL,
  ETag VARCHAR(255) NOT NULL,
  TransactionId VARCHAR(255) NOT NULL,
  TransactionTimestamp TIMESTAMP NOT NULL,
  TransactionManager TEXT NOT NULL,
  StateJson TEXT NULL,
  Timestamp TIMESTAMP NOT NULL,
  PRIMARY KEY (StateId, SequenceId)
);

CREATE INDEX IX_State_Seq ON OrleansTransactionStateTable (StateId, SequenceId);
