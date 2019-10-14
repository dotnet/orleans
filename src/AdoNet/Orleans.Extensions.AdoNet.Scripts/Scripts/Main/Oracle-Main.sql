/*
Implementation notes:

1) The general idea is that data is read and written through Orleans specific queries.
   Orleans operates on column names and types when reading and on parameter names and types when writing.

2) The implementations *must* preserve input and output names and types. Orleans uses these parameters to reads query results by name and type.
   Vendor and deployment specific tuning is allowed and contributions are encouraged as long as the interface contract
   is maintained.

3) The implementation across vendor specific scripts *should* preserve the constraint names. This simplifies troubleshooting
   by virtue of uniform naming across concrete implementations.

5) ETag for Orleans is an opaque column that represents a unique version. The type of its actual implementation
   is not important as long as it represents a unique version. In this implementation we use integers for versioning

6) For the sake of being explicit and removing ambiguity, Orleans expects some queries to return either TRUE as >0 value
   or FALSE as =0 value. That is, affected rows or such does not matter. If an error is raised or an exception is thrown
   the query *must* ensure the entire transaction is rolled back and may either return FALSE or propagate the exception.
   Orleans handles exception as a failure and will retry.

7) The implementation follows the Extended Orleans membership protocol. For more information, see at:
        https://dotnet.github.io/orleans/Documentation/Runtime-Implementation-Details/Runtime-Tables.html
        https://dotnet.github.io/orleans/Documentation/Runtime-Implementation-Details/Cluster-Management.html
        https://github.com/dotnet/orleans/blob/master/src/Orleans.Core/SystemTargetInterfaces/IMembershipTable.cs
*/

-- This table defines Orleans operational queries. Orleans uses these to manage its operations,
-- these are the only queries Orleans issues to the database.
-- These can be redefined (e.g. to provide non-destructive updates) provided the stated interface principles hold.
CREATE TABLE "ORLEANSQUERY"
(
    "QUERYKEY" VARCHAR2(64 BYTE) NOT NULL ENABLE,
    "QUERYTEXT" VARCHAR2(4000 BYTE),

    CONSTRAINT "ORLEANSQUERY_PK" PRIMARY KEY ("QUERYKEY")
);
/

COMMIT;

-- Oracle specific implementation note:
-- Some OrleansQueries are implemented as functions and differ from the scripts of other databases.
-- The main reason for this is the fact, that oracle doesn't support returning variables from queries
-- directly. So in the case that a variable value is needed as output of a OrleansQuery (e.g. version)
-- a function is used.
