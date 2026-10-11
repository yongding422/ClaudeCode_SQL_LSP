CREATE PROCEDURE dbo.ProcessPayroll6 @EmployeeId int AS BEGIN SELECT EmployeeId FROM dbo.Employee6 WHERE EmployeeId = @EmployeeId; END;
GO
EXEC dbo.ProcessPayroll6 @EmployeeId = 42;
