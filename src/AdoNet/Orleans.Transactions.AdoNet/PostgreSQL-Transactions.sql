-- Orleans Transaction Key Table for PostgreSQL
CREATE TABLE OrleansTransactionKeyTable (
  StateId VARCHAR(255) NOT NULL,
  ETag VARCHAR(36) NOT NULL,
  CommittedSequenceId BIGINT NOT NULL,
  Metadata BYTEA NULL,
  Timestamp timestamptz(3) NOT NULL DEFAULT now(),
  PRIMARY KEY (StateId)
);

-- Orleans Transaction State Table for PostgreSQL
CREATE TABLE OrleansTransactionStateTable (
  StateId VARCHAR(255) NOT NULL,
  SequenceId BIGINT NOT NULL,
  ETag VARCHAR(36) NOT NULL,
  TransactionId VARCHAR(36) NOT NULL,
  TransactionTimestamp TIMESTAMP NOT NULL,
  TransactionManager BYTEA NOT NULL,
  SateData BYTEA NULL,
  Timestamp timestamptz(3) NOT NULL DEFAULT now(),
  PRIMARY KEY (StateId, SequenceId)
);

CREATE INDEX IX_State_Seq ON OrleansTransactionStateTable (StateId, SequenceId);
