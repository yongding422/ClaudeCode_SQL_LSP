CREATE PROCEDURE dbo.ProcessPayroll4 @EmployeeId int AS BEGIN SELECT EmployeeId FROM dbo.Employee4 WHERE EmployeeId = @EmployeeId; END;
GO
EXEC dbo.ProcessPayroll4 @EmployeeId = 42;
