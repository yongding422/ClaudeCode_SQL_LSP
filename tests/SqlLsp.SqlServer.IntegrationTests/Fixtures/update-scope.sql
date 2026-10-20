-- Two different stock items; each has exactly one source match.
-- Only purchase order 1 was requested. Order 2 must remain unchanged.
CREATE TABLE #Stock(ItemId int PRIMARY KEY, Quantity int NOT NULL);
CREATE TABLE #Lines(OrderId int NOT NULL, ItemId int NOT NULL, Received int NOT NULL);
INSERT #Stock VALUES (1,100),(2,100);
INSERT #Lines VALUES (1,1,10),(2,2,20);

UPDATE s SET Quantity += l.Received
FROM #Stock AS s JOIN #Lines AS l ON s.ItemId=l.ItemId;
DECLARE @BroadOther int=(SELECT Quantity FROM #Stock WHERE ItemId=2);

UPDATE #Stock SET Quantity=100;
UPDATE s SET Quantity += l.Received
FROM #Stock AS s JOIN #Lines AS l ON s.ItemId=l.ItemId
WHERE l.OrderId=1;

SELECT @BroadOther AS BroadOtherOrder,
       (SELECT Quantity FROM #Stock WHERE ItemId=1) AS ScopedRequestedOrder,
       (SELECT Quantity FROM #Stock WHERE ItemId=2) AS ScopedOtherOrder;
