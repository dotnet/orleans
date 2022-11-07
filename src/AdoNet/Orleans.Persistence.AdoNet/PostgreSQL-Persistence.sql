CREATE TABLE OrleansStorage
(
    grainidhash integer NOT NULL,
    grainidkey character varying(512) ,
    graintypehash integer NOT NULL,
    graintypestring character varying(512)  NOT NULL,
    serviceid character varying(150)  NOT NULL,
    payloadbinary bytea,
    modifiedon timestamp without time zone NOT NULL,
    version integer
);

CREATE INDEX ix_orleansstorage
    ON orleansstorage USING btree
    (grainidhash, graintypehash);

CREATE OR REPLACE FUNCTION writetostorage(
    _grainidhash integer,
    _grainidkey character varying,
    _graintypehash integer,
    _graintypestring character varying,
    _serviceid character varying,
    _grainstateversion integer,
    _payloadbinary bytea)
    RETURNS TABLE(newgrainstateversion integer)
    LANGUAGE 'plpgsql'
AS $function$
    DECLARE
     _newGrainStateVersion integer := _GrainStateVersion;
     RowCountVar integer := 0;

    BEGIN

    -- Grain state is not null, so the state must have been read from the storage before.
    -- Let's try to update it.
    --
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
    -- See further information at https://docs.microsoft.com/dotnet/orleans/grains/grain-persistence.
    IF _GrainStateVersion IS NOT NULL
    THEN
        UPDATE OrleansStorage
        SET
            PayloadBinary = _PayloadBinary,
            ModifiedOn = (now() at time zone 'utc'),
            Version = Version + 1

        WHERE
            GrainIdHash = _GrainIdHash AND _GrainIdHash IS NOT NULL
            AND GrainTypeHash = _GrainTypeHash AND _GrainTypeHash IS NOT NULL
            AND GrainIdKey = _GrainIdKey AND _GrainIdKey IS NOT NULL
            AND GrainTypeString = _GrainTypeString AND _GrainTypeString IS NOT NULL
            AND ServiceId = _ServiceId AND _ServiceId IS NOT NULL
            AND Version IS NOT NULL AND Version = _GrainStateVersion AND _GrainStateVersion IS NOT NULL;

        GET DIAGNOSTICS RowCountVar = ROW_COUNT;
        IF RowCountVar > 0
        THEN
            _newGrainStateVersion := _GrainStateVersion + 1;
        END IF;
    END IF;

    -- The grain state has not been read. The following locks rather pessimistically
    -- to ensure only one INSERT succeeds.
    IF _GrainStateVersion IS NULL
    THEN
        INSERT INTO OrleansStorage
        (
            GrainIdHash,
            GrainIdKey,
            GrainTypeHash,
            GrainTypeString,
            ServiceId,
            PayloadBinary,
            ModifiedOn,
            Version
        )
        SELECT
            _GrainIdHash,
            _GrainIdKey,
            _GrainTypeHash,
            _GrainTypeString,
            _ServiceId,
            _PayloadBinary,
           (now() at time zone 'utc'),
            1
        WHERE NOT EXISTS
         (
            -- There should not be any version of this grain state.
            SELECT 1
            FROM OrleansStorage
            WHERE
                GrainIdHash = _GrainIdHash AND _GrainIdHash IS NOT NULL
                AND GrainTypeHash = _GrainTypeHash AND _GrainTypeHash IS NOT NULL
                AND GrainIdKey = _GrainIdKey AND _GrainIdKey IS NOT NULL
                AND GrainTypeString = _GrainTypeString AND _GrainTypeString IS NOT NULL
                AND ServiceId = _ServiceId AND _ServiceId IS NOT NULL
         );

        GET DIAGNOSTICS RowCountVar = ROW_COUNT;
        IF RowCountVar > 0
        THEN
            _newGrainStateVersion := 1;
        END IF;
    END IF;

    RETURN QUERY SELECT _newGrainStateVersion AS NewGrainStateVersion;
END

$function$;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'WriteToStorageKey','

        select * from WriteToStorage(@GrainIdHash, @GrainIdKey, @GrainTypeHash, @GrainTypeString, @ServiceId, @GrainStateVersion, @PayloadBinary);
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'ReadFromStorageKey','
    SELECT
        PayloadBinary,
        (now() at time zone ''utc''),
        Version
    FROM
        OrleansStorage
    WHERE
        GrainIdHash = @GrainIdHash
        AND GrainTypeHash = @GrainTypeHash AND @GrainTypeHash IS NOT NULL
        AND GrainIdKey = @GrainIdKey AND GrainIdKey IS NOT NULL
        AND GrainTypeString = @GrainTypeString AND GrainTypeString IS NOT NULL
        AND ServiceId = @ServiceId AND @ServiceId IS NOT NULL
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'ClearStorageKey','
    UPDATE OrleansStorage
    SET
        PayloadBinary = NULL,
        Version = Version + 1
    WHERE
        GrainIdHash = @GrainIdHash AND @GrainIdHash IS NOT NULL
        AND GrainTypeHash = @GrainTypeHash AND @GrainTypeHash IS NOT NULL
        AND GrainIdKey = @GrainIdKey AND GrainIdKey IS NOT NULL
        AND GrainTypeString = @GrainTypeString AND @GrainTypeString IS NOT NULL
        AND ServiceId = @ServiceId AND @ServiceId IS NOT NULL
        AND Version IS NOT NULL AND Version = @GrainStateVersion AND @GrainStateVersion IS NOT NULL
    Returning Version as NewGrainStateVersion
');
