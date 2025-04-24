-- Orleans Transaction Key Table for Oracle
CREATE TABLE OrleansTransactionKeyTable (
  StateId VARCHAR2(255) NOT NULL,
  ETag VARCHAR2(255) NOT NULL,
  CommittedSequenceId NUMBER(19) NOT NULL,
  Metadata CLOB NULL,
  Timestamp TIMESTAMP NOT NULL,
  PRIMARY KEY (StateId)
);

-- Orleans Transaction State Table for Oracle
CREATE TABLE OrleansTransactionStateTable (
  StateId VARCHAR2(255) NOT NULL,
  SequenceId NUMBER(19) NOT NULL,
  ETag VARCHAR2(255) NOT NULL,
  TransactionId VARCHAR2(255) NOT NULL,
  TransactionTimestamp TIMESTAMP NOT NULL,
  TransactionManager CLOB NOT NULL,
  StateJson CLOB NULL,
  Timestamp TIMESTAMP NOT NULL,
  PRIMARY KEY (StateId, SequenceId)
);

CREATE INDEX IX_State_Seq ON OrleansTransactionStateTable (StateId, SequenceId);
