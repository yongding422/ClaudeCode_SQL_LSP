CREATE PROCEDURE dbo.ProcessPayroll3 @EmployeeId int AS BEGIN SELECT EmployeeId FROM dbo.Employee3 WHERE EmployeeId = @EmployeeId; END;
GO
EXEC dbo.ProcessPayroll3 @EmployeeId = 42;
