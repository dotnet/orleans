-- Some known constants merged to the table. Note that not mathced ones could be purged too.
MERGE [Core].[ConstantsExample] AS TARGET
USING
(
	VALUES
		(1, N'Constant 1'),
		(2, N'Constant 2'),
		(3, N'Constant 3'),
		(4, N'Constant 4'),
		(5, N'Constant 5')
) AS SOURCE
(
	[Id],
	[Expression]
)
ON TARGET.[Id] = SOURCE.[Id]
-- Update matched rows.
WHEN MATCHED THEN UPDATE SET
	TARGET.[Expression] = SOURCE.[Expression]
-- Insert these as new rows.
WHEN NOT MATCHED BY TARGET THEN INSERT
(
	[Id],
	[Expression]
) VALUES
(
	SOURCE.[Id],
	SOURCE.[Expression]
);

GO