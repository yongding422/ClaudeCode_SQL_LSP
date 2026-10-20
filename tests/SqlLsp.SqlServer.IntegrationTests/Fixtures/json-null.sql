DECLARE @Data nvarchar(max)=N'{"Events":[{"Status":"Receiver not present"}],"DeliveredWhen":"2025-01-02T10:00:00"}';
SELECT CASE WHEN JSON_VALUE(@Data,'$.Status') IS NULL THEN 1 ELSE 0 END AS RootStatusMissing,
       CASE WHEN JSON_VALUE(@Data,'$.Status') <> 'Delivered' THEN 1 ELSE 0 END AS RootNotDeliveredMatches,
       CASE WHEN JSON_VALUE(@Data,'$.Events[0].Status') <> 'Delivered' THEN 1 ELSE 0 END AS EventNotDeliveredMatches,
       CASE WHEN TRY_CONVERT(datetime2,JSON_VALUE(@Data,'$.DeliveredWhen')) IS NOT NULL THEN 1 ELSE 0 END AS DeliveryTimePresent;
