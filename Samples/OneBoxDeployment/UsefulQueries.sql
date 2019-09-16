-- If the database gets stuck to single user mode.
ALTER DATABASE [OneBoxDeployment.Database] SET MULTI_USER;

-- Check what data there is in version tables if the system takes long time to start.
SELECT * FROM [OneBoxDeployment.Database].[Orleans].[OrleansMembershipTable];
SELECT * FROM [OneBoxDeployment.Database].[Orleans].[OrleansMembershipVersionTable];

-- Just remove everything.
DELETE [OneBoxDeployment.Database].[Orleans].[OrleansMembershipTable];
DELETE [OneBoxDeployment.Database].[Orleans].[OrleansMembershipVersionTable];