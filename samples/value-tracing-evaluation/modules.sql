CREATE PROCEDURE dbo.RoutePayroll @State varchar(2), @UseOverride bit=0 AS
BEGIN
    DECLARE @Region varchar(2)=@State;
    IF @UseOverride=1 SET @Region='CA';
    EXEC dbo.ApplyRegion @Region=@Region;
END;
GO
CREATE PROCEDURE dbo.ApplyRegion @Region varchar(2), @Mode int=7 AS
BEGIN
    SELECT * FROM dbo.Employee WHERE StateCode=@Region AND Eligibility>=@Mode;
END;
GO
CREATE PROCEDURE dbo.DefaultRoute @State varchar(2) AS
BEGIN
    EXEC dbo.ApplyRegion @Region=@State,@Mode=DEFAULT;
    EXEC dbo.ApplyRegion @Region=@State;
END;
GO
CREATE PROCEDURE dbo.OutputRoute @Seed int AS
BEGIN
    DECLARE @Result int=@Seed;
    DECLARE @Status int;
    EXEC @Status=dbo.Calculate @Input=@Result,@Output=@Result OUTPUT;
    IF @Result>0 SELECT * FROM dbo.Employee WHERE Eligibility=@Result;
END;
GO
CREATE PROCEDURE dbo.Calculate @Input int,@Output int OUTPUT AS
BEGIN
    SET @Output=@Input;
    RETURN 42;
END;
GO
CREATE PROCEDURE dbo.SelectRetention @Seed int AS
BEGIN
    DECLARE @Result int=@Seed;
    SELECT @Result=Eligibility FROM dbo.Employee WHERE StateCode='OH';
    IF @Result>0 RETURN;
END;
GO
CREATE PROCEDURE dbo.LoopRoute @Seed int AS
BEGIN
    DECLARE @Result int=@Seed;
    WHILE @Seed>0
    BEGIN
        IF @Result>0 BREAK;
        SET @Result=3;
        SET @Seed=0;
    END;
    RETURN @Result;
END;
GO
CREATE PROCEDURE dbo.InvalidRoute @State varchar(2) AS
BEGIN
    EXEC dbo.ApplyRegion @Wrong=@State;
END;
GO
CREATE TABLE dbo.Employee(StateCode varchar(2),Eligibility int);
