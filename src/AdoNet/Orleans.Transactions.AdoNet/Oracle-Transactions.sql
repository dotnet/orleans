-- Orleans Transaction Key Table for Oracle
CREATE TABLE OrleansTransactionKeyTable (
  StateId VARCHAR2(255) NOT NULL,
  ETag VARCHAR2(36) NOT NULL,
  CommittedSequenceId NUMBER(19) NOT NULL,
  Metadata BLOB NULL,
  Timestamp TIMESTAMP (6) DEFAULT sys_extract_utc(systimestamp) NOT NULL ENABLE,
  PRIMARY KEY (StateId)
);

-- Orleans Transaction State Table for Oracle
CREATE TABLE OrleansTransactionStateTable (
  StateId VARCHAR2(255) NOT NULL,
  SequenceId NUMBER(19) NOT NULL,
  ETag VARCHAR2(36) NOT NULL,
  TransactionId VARCHAR2(36) NOT NULL,
  TransactionTimestamp TIMESTAMP NOT NULL,
  TransactionManager BLOB NOT NULL,
  SateData BLOB NULL,
  Timestamp TIMESTAMP (6) DEFAULT sys_extract_utc(systimestamp) NOT NULL ENABLE,
  PRIMARY KEY (StateId, SequenceId)
);

CREATE INDEX IX_State_Seq ON OrleansTransactionStateTable (StateId, SequenceId);
