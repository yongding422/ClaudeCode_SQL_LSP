CREATE FUNCTION dbo.AdjustPay3 (@amount int) RETURNS int AS BEGIN RETURN @amount + 3; END;
