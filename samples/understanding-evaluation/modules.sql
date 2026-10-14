CREATE PROCEDURE dbo.ProcessPayroll @State varchar(2) AS
BEGIN
    INSERT dbo.PayrollAudit(StateCode)
    SELECT StateCode FROM dbo.Employee WHERE StateCode = @State;
    IF @State = 'OH'
        EXEC dbo.ApplyOhioRules @State = @State;
    EXEC(N'SELECT * FROM dbo.DynamicOnly');
END;
GO
CREATE PROCEDURE dbo.ApplyOhioRules @State varchar(2) AS
BEGIN
    UPDATE p SET TaxAdjusted = 1
    FROM dbo.Payroll p JOIN dbo.Employee e ON e.EmployeeId = p.EmployeeId
    WHERE e.StateCode = @State;
END;
GO
CREATE TABLE dbo.Employee(EmployeeId int, StateCode varchar(2));
GO
CREATE TABLE dbo.Payroll(EmployeeId int, TaxAdjusted bit);
GO
CREATE TABLE dbo.PayrollAudit(StateCode varchar(2));
GO
-- A name collision that must not be selected as the dbo entry point.
CREATE PROCEDURE audit.ProcessPayroll AS SELECT 1;
