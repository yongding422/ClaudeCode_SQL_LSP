CREATE FUNCTION dbo.AdjustPay2 (@amount int) RETURNS int AS BEGIN RETURN @amount + 2; END;
