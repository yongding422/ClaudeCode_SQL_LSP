CREATE PROCEDURE dbo.RunPayroll AS
BEGIN
    EXEC dbo.ProcessPayroll @PayrollId = 42;
END;
