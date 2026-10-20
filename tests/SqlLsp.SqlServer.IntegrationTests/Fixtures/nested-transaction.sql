CREATE TABLE #Changes(Id int NOT NULL);
BEGIN TRANSACTION;
INSERT #Changes VALUES (1);
BEGIN TRANSACTION;
INSERT #Changes VALUES (2);
COMMIT TRANSACTION;
DECLARE @AfterInnerCommit int=@@TRANCOUNT;
ROLLBACK TRANSACTION;
SELECT @AfterInnerCommit AS AfterInnerCommit,
       @@TRANCOUNT AS AfterOuterRollback,
       (SELECT COUNT(*) FROM #Changes) AS PersistedChanges;
