CREATE PROCEDURE dbo.ProcessPayroll @PayrollId int AS
BEGIN
    SELECT PayrollId FROM dbo.Payroll WHERE PayrollId = @PayrollId;
END;
