-- If the database gets stuck to single user mode.
ALTER DATABASE [BigSample.Database] SET MULTI_USER;

-- Check what data there is in version tables if the system takes long time to start.
SELECT * FROM [BigSample.Database].Orleans.OrleansMembershipTable;
SELECT * FROM [BigSample.Database].Orleans.OrleansMembershipVersionTable;

-- Just remove everything.
DELETE [BigSample.Database].Orleans.OrleansMembershipTable;
DELETE [BigSample.Database].Orleans.OrleansMembershipVersionTable;