-- Orleans Transaction table for mysql
CREATE TABLE OrleansTransactionKeyTable  (
  StateId varchar(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
  ETag varchar(36) NOT NULL,
  CommittedSequenceId bigint(20) NOT NULL,
  Metadata LONGBLOB NULL,
  Timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (StateId)
);

-- Orleans Transaction State Table for mysql
CREATE TABLE OrleansTransactionStateTable  (
  StateId varchar(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
  SequenceId bigint(20) NOT NULL,
  ETag varchar(36)  NOT NULL,
  TransactionId varchar(36) NOT NULL,
  TransactionTimestamp DATETIME NOT NULL,
  TransactionManager LONGBLOB NOT NULL,
  StateData LONGBLOB NULL,
  Timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (StateId, SequenceId),
  INDEX IX_State_Seq(StateId, SequenceId) USING BTREE
);
