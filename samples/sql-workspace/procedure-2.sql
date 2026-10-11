CREATE PROCEDURE dbo.ProcessPayroll2 @EmployeeId int AS BEGIN SELECT EmployeeId FROM dbo.Employee2 WHERE EmployeeId = @EmployeeId; END;
GO
EXEC dbo.ProcessPayroll2 @EmployeeId = 42;
