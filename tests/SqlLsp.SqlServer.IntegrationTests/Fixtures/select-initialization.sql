-- A newly declared scalar starts NULL. An initialized scalar retains its
-- existing value when SELECT assigns no rows. These are different cases.
DECLARE @Fresh int;
DECLARE @Initialized int=99;
SELECT @Fresh=7 WHERE 1=0;
SELECT @Initialized=7 WHERE 1=0;
SELECT @Fresh AS FreshNoMatch, @Initialized AS InitializedNoMatch;
