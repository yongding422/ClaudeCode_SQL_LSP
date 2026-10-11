CREATE PROCEDURE dbo.ProcessPayroll1 @EmployeeId int AS BEGIN SELECT EmployeeId FROM dbo.Employee1 WHERE EmployeeId = @EmployeeId; END;
GO
EXEC dbo.ProcessPayroll1 @EmployeeId = 42;
