-- Orleans Transaction table
CREATE TABLE OrleansTransactionKeyTable  (
  StateId varchar(255) NOT NULL,
  ETag varchar(255) NOT NULL,
  CommittedSequenceId bigint(20) NOT NULL,
  Metadata text  NULL,
  Timestamp DATETIME NOT NULL,
  PRIMARY KEY (StateId)
);

CREATE TABLE OrleansTransactionStateTable  (
  StateId varchar(255)  NOT NULL,
  SequenceId bigint(20) NOT NULL,
  ETag varchar(255)  NOT NULL,
  TransactionId varchar(255) NOT NULL,
  TransactionTimestamp DATETIME NOT NULL,
  TransactionManager text  NOT NULL,
  StateJson text  NULL,
  Timestamp DATETIME NOT NULL,
  PRIMARY KEY (StateId, SequenceId),
  INDEX IX_State_Seq(StateId, SequenceId) USING BTREE
);

