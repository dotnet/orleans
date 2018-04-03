-- The design criteria for this table are:
--
-- 1. It can contain arbitrary content serialized as binary, XML or JSON. These formats
-- are supported to allow one to take advantage of in-storage processing capabilities for
-- these types if required. This should not incur extra cost on storage.
--
-- 2. The table design should scale with the idea of tens or hundreds (or even more) types
-- of grains that may operate with even hundreds of thousands of grain IDs within each
-- type of a grain.
--
-- 3. The table and its associated operations should remain stable. There should not be
-- structural reason for unexpected delays in operations. It should be possible to also
-- insert data reasonably fast without resource contention.
--
-- 4. For reasons in 2. and 3., the index should be as narrow as possible so it fits well in
-- memory and should it require maintenance, isn't resource intensive. For this
-- reason the index is narrow by design (ideally non-clustered). Currently the entity
-- is recognized in the storage by the grain type and its ID, which are unique in Orleans silo.
-- The ID is the grain ID bytes (if string type UTF-8 bytes) and possible extension key as UTF-8
-- bytes concatenated with the ID and then hashed.
--
-- Reason for hashing: Database engines usually limit the length of the column sizes, which
-- would artificially limit the length of IDs or types. Even when within limitations, the
-- index would be thick and consume more memory.
--
-- In the current setup the ID and the type are hashed into two INT type instances, which
-- are made a compound index. When there are no collisions, the index can quickly locate
-- the unique row. Along with the hashed index values, the NVARCHAR(nnn) values are also
-- stored and they are used to prune hash collisions down to only one result row.
--
-- 5. The design leads to duplication in the storage. It is reasonable to assume there will
-- a low number of services with a given service ID operational at any given time. Or that
-- compared to the number of grain IDs, there are a fairly low number of different types of
-- grain. The catch is that were these data separated to another table, it would make INSERT
-- and UPDATE operations complicated and would require joins, temporary variables and additional
-- indexes or some combinations of them to make it work. It looks like fitting strategy
-- could be to use table compression.
--
-- 6. For the aforementioned reasons, grain state DELETE will set NULL to the data fields
-- and updates the Version number normally. This should alleviate the need for index or
-- statistics maintenance with the loss of some bytes of storage space. The table can be scrubbed
-- in a separate maintenance operation.
--
-- 7. In the storage operations queries the columns need to be in the exact same order
-- since the storage table operations support optionally streaming.
CREATE TABLE "STORAGE"
(

    -- These are for the book keeping. Orleans calculates
    -- these hashes (see RelationalStorageProvide implementation),
    -- which are signed 32 bit integers mapped to the *Hash fields.
    -- The mapping is done in the code. The
    -- *String columns contain the corresponding clear name fields.
    --
    -- If there are duplicates, they are resolved by using GrainIdN0,
    -- GrainIdN1, GrainIdExtensionString and GrainTypeString fields.
    -- It is assumed these would be rarely needed.
    "GRAINIDHASH" NUMBER(*,0) NOT NULL ENABLE,
    "GRAINIDN0" NUMBER(19,0) NOT NULL ENABLE,
    "GRAINIDN1" NUMBER(19,0) NOT NULL ENABLE,
    "GRAINTYPEHASH" NUMBER(*,0) NOT NULL ENABLE,
    "GRAINTYPESTRING" NVARCHAR2(512) NOT NULL ENABLE,
    "GRAINIDEXTENSIONSTRING" NVARCHAR2(512),
    "SERVICEID" NVARCHAR2(150) NOT NULL ENABLE,


    -- The usage of the Payload records is exclusive in that
    -- only one should be populated at any given time and two others
    -- are NULL. The types are separated to advantage on special
    -- processing capabilities present on database engines (not all might
    -- have both JSON and XML types.
    --
    -- One is free to alter the size of these fields.
    "PAYLOADBINARY" BLOB,
    "PAYLOADXML" CLOB,
    "PAYLOADJSON" CLOB,
    -- Informational field, no other use.
    "MODIFIEDON" TIMESTAMP (6) NOT NULL ENABLE,
    -- The version of the stored payload.
    "VERSION" NUMBER(*,0)

    -- The following would in principle be the primary key, but it would be too thick
    -- to be indexed, so the values are hashed and only collisions will be solved
    -- by using the fields. That is, after the indexed queries have pinpointed the right
    -- rows down to [0, n] relevant ones, n being the number of collided value pairs.
);
CREATE INDEX "IX_STORAGE" ON "STORAGE" ("GRAINIDHASH", "GRAINTYPEHASH") PARALLEL
COMPRESS;
/

CREATE OR REPLACE FUNCTION WriteToStorage(PARAM_GRAINIDHASH IN NUMBER, PARAM_GRAINIDN0 IN NUMBER, PARAM_GRAINIDN1 IN NUMBER, PARAM_GRAINTYPEHASH IN NUMBER, PARAM_GRAINTYPESTRING IN NVARCHAR2,
                                             PARAM_GRAINIDEXTENSIONSTRING IN NVARCHAR2, PARAM_SERVICEID IN VARCHAR2, PARAM_GRAINSTATEVERSION IN NUMBER, PARAM_PAYLOADBINARY IN BLOB,
                                             PARAM_PAYLOADJSON IN CLOB, PARAM_PAYLOADXML IN CLOB)
  RETURN NUMBER IS
  rowcount NUMBER;
  newGrainStateVersion NUMBER := PARAM_GRAINSTATEVERSION;
  PRAGMA AUTONOMOUS_TRANSACTION;
  BEGIN
    -- When Orleans is running in normal, non-split state, there will
    -- be only one grain with the given ID and type combination only. This
    -- grain saves states mostly serially if Orleans guarantees are upheld. Even
    -- if not, the updates should work correctly due to version number.
    --
    -- In split brain situations there can be a situation where there are two or more
    -- grains with the given ID and type combination. When they try to INSERT
    -- concurrently, the table needs to be locked pessimistically before one of
    -- the grains gets @GrainStateVersion = 1 in return and the other grains will fail
    -- to update storage. The following arrangement is made to reduce locking in normal operation.
    --
    -- If the version number explicitly returned is still the same, Orleans interprets it so the update did not succeed
    -- and throws an InconsistentStateException.
    --
    -- See further information at http://dotnet.github.io/orleans/Getting-Started-With-Orleans/Grain-Persistence.


    -- If the @GrainStateVersion is not zero, this branch assumes it exists in this database.
    -- The NULL value is supplied by Orleans when the state is new.
    IF newGrainStateVersion IS NOT NULL THEN
        UPDATE Storage
        SET
            PayloadBinary = PARAM_PAYLOADBINARY,
            PayloadJson = PARAM_PAYLOADJSON,
            PayloadXml = PARAM_PAYLOADXML,
            ModifiedOn = sys_extract_utc(systimestamp),
            Version = Version + 1
        WHERE
            GrainIdHash = PARAM_GRAINIDHASH AND PARAM_GRAINIDHASH IS NOT NULL
            AND GrainTypeHash = PARAM_GRAINTYPEHASH AND PARAM_GRAINTYPEHASH IS NOT NULL
            AND (GrainIdN0 = PARAM_GRAINIDN0 OR PARAM_GRAINIDN0 IS NULL)
            AND (GrainIdN1 = PARAM_GRAINIDN1 OR PARAM_GRAINIDN1 IS NULL)
            AND (GrainTypeString = PARAM_GRAINTYPESTRING OR PARAM_GRAINTYPESTRING IS NULL)
            AND ((PARAM_GRAINIDEXTENSIONSTRING IS NOT NULL AND GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString = PARAM_GRAINIDEXTENSIONSTRING) OR PARAM_GRAINIDEXTENSIONSTRING IS NULL AND GrainIdExtensionString IS NULL)
            AND ServiceId = PARAM_SERVICEID AND PARAM_SERVICEID IS NOT NULL
            AND Version IS NOT NULL AND Version = PARAM_GRAINSTATEVERSION AND PARAM_GRAINSTATEVERSION IS NOT NULL
    RETURNING Version INTO newGrainStateVersion;

    rowcount := SQL%ROWCOUNT;

    IF rowcount = 1 THEN
      COMMIT;
      RETURN(newGrainStateVersion);
    END IF;
    END IF;

    -- The grain state has not been read. The following locks rather pessimistically
    -- to ensure only one INSERT succeeds.
    IF PARAM_GRAINSTATEVERSION IS NULL THEN
        INSERT INTO Storage
        (
            GrainIdHash,
            GrainIdN0,
            GrainIdN1,
            GrainTypeHash,
            GrainTypeString,
            GrainIdExtensionString,
            ServiceId,
            PayloadBinary,
            PayloadJson,
            PayloadXml,
            ModifiedOn,
            Version
        )
        SELECT
            PARAM_GRAINIDHASH,
            PARAM_GRAINIDN0,
            PARAM_GRAINIDN1,
            PARAM_GRAINTYPEHASH,
            PARAM_GRAINTYPESTRING,
            PARAM_GRAINIDEXTENSIONSTRING,
            PARAM_SERVICEID,
            PARAM_PAYLOADBINARY,
            PARAM_PAYLOADJSON,
            PARAM_PAYLOADXML,
            sys_extract_utc(systimestamp),
            1 FROM DUAL
         WHERE NOT EXISTS
         (
            -- There should not be any version of this grain state.
            SELECT 1
            FROM Storage
            WHERE
                GrainIdHash = PARAM_GRAINIDHASH AND PARAM_GRAINIDHASH IS NOT NULL
                AND GrainTypeHash = PARAM_GRAINTYPEHASH AND PARAM_GRAINTYPEHASH IS NOT NULL
                AND (GrainIdN0 = PARAM_GRAINIDN0 OR PARAM_GRAINIDN0 IS NULL)
                AND (GrainIdN1 = PARAM_GRAINIDN1 OR PARAM_GRAINIDN1 IS NULL)
                AND (GrainTypeString = PARAM_GRAINTYPESTRING OR PARAM_GRAINTYPESTRING IS NULL)
                AND ((PARAM_GRAINIDEXTENSIONSTRING IS NOT NULL AND GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString = PARAM_GRAINIDEXTENSIONSTRING) OR PARAM_GRAINIDEXTENSIONSTRING IS NULL AND GrainIdExtensionString IS NULL)
                AND ServiceId = PARAM_SERVICEID AND PARAM_SERVICEID IS NOT NULL
         );

     rowCount := SQL%ROWCOUNT;

        IF rowCount > 0 THEN
            newGrainStateVersion := 1;
        END IF;
    END IF;
  COMMIT;
    RETURN(newGrainStateVersion);
  END;
/

CREATE OR REPLACE FUNCTION ClearStorage(PARAM_GRAINIDHASH IN NUMBER, PARAM_GRAINIDN0 IN NUMBER, PARAM_GRAINIDN1 IN NUMBER, PARAM_GRAINTYPEHASH IN NUMBER, PARAM_GRAINTYPESTRING IN NVARCHAR2,
                                             PARAM_GRAINIDEXTENSIONSTRING IN NVARCHAR2, PARAM_SERVICEID IN VARCHAR2, PARAM_GRAINSTATEVERSION IN NUMBER)
  RETURN NUMBER IS
  rowcount NUMBER;
  newGrainStateVersion NUMBER := PARAM_GRAINSTATEVERSION;
  PRAGMA AUTONOMOUS_TRANSACTION;
  BEGIN
    UPDATE Storage
    SET
        PayloadBinary = NULL,
        PayloadJson = NULL,
        PayloadXml = NULL,
        ModifiedOn = sys_extract_utc(systimestamp),
        Version = Version + 1
    WHERE GrainIdHash = PARAM_GRAINIDHASH AND PARAM_GRAINIDHASH IS NOT NULL
      AND GrainTypeHash = PARAM_GRAINTYPEHASH AND PARAM_GRAINTYPEHASH IS NOT NULL
      AND (GrainIdN0 = PARAM_GRAINIDN0 OR PARAM_GRAINIDN0 IS NULL)
      AND (GrainIdN1  = PARAM_GRAINIDN1 OR PARAM_GRAINIDN1 IS NULL)
      AND (GrainTypeString = PARAM_GRAINTYPESTRING OR PARAM_GRAINTYPESTRING IS NULL)
      AND ((PARAM_GRAINIDEXTENSIONSTRING IS NOT NULL AND GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString = PARAM_GRAINIDEXTENSIONSTRING) OR PARAM_GRAINIDEXTENSIONSTRING IS NULL AND GrainIdExtensionString IS NULL)
      AND ServiceId = PARAM_SERVICEID AND PARAM_SERVICEID IS NOT NULL
      AND Version IS NOT NULL AND Version = PARAM_GRAINSTATEVERSION AND PARAM_GRAINSTATEVERSION IS NOT NULL
    RETURNING Version INTO newGrainStateVersion;

    COMMIT;
    RETURN(newGrainStateVersion);
  END;
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'WriteToStorageKey','
  SELECT WriteToStorage(:GrainIdHash, :GrainIdN0, :GrainIdN1, :GrainTypeHash, :GrainTypeString,
                                             :GrainIdExtensionString, :ServiceId, :GrainStateVersion, :PayloadBinary,
                                             :PayloadJson, :PayloadXml) AS NewGrainStateVersion FROM DUAL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'ClearStorageKey',
    'SELECT ClearStorage(:GrainIdHash, :GrainIdN0, :GrainIdN1, :GrainTypeHash, :GrainTypeString,
                                             :GrainIdExtensionString, :ServiceId, :GrainStateVersion) AS Version FROM DUAL'
);
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'ReadFromStorageKey',
    '
     SELECT PayloadBinary, PayloadXml, PayloadJson, Version
     FROM Storage
     WHERE GrainIdHash = :GrainIdHash AND :GrainIdHash IS NOT NULL
       AND (GrainIdN0 = :GrainIdN0 OR :GrainIdN0 IS NULL)
       AND (GrainIdN1 = :GrainIdN1 OR :GrainIdN1 IS NULL)
       AND GrainTypeHash = :GrainTypeHash AND :GrainTypeHash IS NOT NULL
       AND (GrainTypeString = :GrainTypeString OR :GrainTypeString IS NULL)
       AND ((:GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString = :GrainIdExtensionString) OR :GrainIdExtensionString IS NULL AND GrainIdExtensionString IS NULL)
       AND ServiceId = :ServiceId AND :ServiceId IS NOT NULL'
);
/

COMMIT;
