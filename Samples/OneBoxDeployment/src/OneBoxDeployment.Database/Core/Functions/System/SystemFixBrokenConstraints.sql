/*
  Uses the view SystemBrokenConstraint to find broken constraints and by building
  a SQL command to fix the constraints and executing it.
  Usually broken constraints appear when the constraints are temporarily disabled during
  a performance intensive operation such as bulk-loading data in the database. If the
  constraints aren't fixed, the database don't use them, which potentially incurs a significant
  performance hit.
*/
CREATE PROCEDURE [Core].[SystemFixBrokenConstraints]
AS
	SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
	SET NOCOUNT, XACT_ABORT ON;

	DECLARE @constraintsToFix NVARCHAR(MAX);
	SELECT @constraintsToFix = ISNULL(@constraintsToFix, N'') +	N' ALTER TABLE ' + bc.TableName + N' WITH CHECK CHECK CONSTRAINT ' + bc.BrokenConstraintName + ';'
	FROM SystemBrokenConstraintsView AS bc;

	BEGIN TRANSACTION;
		EXEC(@constraintsToFix);
	COMMIT TRANSACTION;
RETURN 0