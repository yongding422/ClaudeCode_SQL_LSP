CREATE PROCEDURE dbo.ProcessPayroll5 @EmployeeId int AS BEGIN SELECT EmployeeId FROM dbo.Employee5 WHERE EmployeeId = @EmployeeId; END;
GO
EXEC dbo.ProcessPayroll5 @EmployeeId = 42;
