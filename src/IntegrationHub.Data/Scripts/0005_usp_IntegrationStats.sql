-- Integration health per source system per day: volumes by status, average processing latency, failure rate.
CREATE OR ALTER PROCEDURE dbo.usp_IntegrationStats
    @From date,
    @To   date
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @fromTs datetime2(3) = @From,
            @toTs   datetime2(3) = DATEADD(day, 1, CAST(@To AS datetime2(3)));

    WITH scoped AS
    (
        SELECT i.source_system,
               CAST(i.received_at AS date) AS stat_date,
               i.status,
               i.attempt_count,
               CASE WHEN i.status = N'Processed'
                    THEN DATEDIFF_BIG(millisecond, i.received_at, i.processed_at) END AS latency_ms
        FROM   dbo.inbox AS i
        WHERE  i.received_at >= @fromTs
          AND  i.received_at <  @toTs
    )
    SELECT   source_system,
             stat_date,
             COUNT(*)                                                               AS total,
             SUM(CASE WHEN status = N'Processed' THEN 1 ELSE 0 END)                 AS processed,
             SUM(CASE WHEN status IN (N'Received', N'Processing') THEN 1 ELSE 0 END) AS pending,
             SUM(CASE WHEN status = N'Failed' THEN 1 ELSE 0 END)                    AS retrying,
             SUM(CASE WHEN status = N'DeadLettered' THEN 1 ELSE 0 END)              AS dead_lettered,
             SUM(CASE WHEN attempt_count > 1 THEN 1 ELSE 0 END)                     AS needed_retry,
             AVG(latency_ms)                                                        AS avg_latency_ms,
             CAST(SUM(CASE WHEN status = N'DeadLettered' THEN 1.0 ELSE 0 END) / COUNT(*) AS decimal(5,4)) AS failure_rate
    FROM     scoped
    GROUP BY source_system, stat_date
    ORDER BY stat_date, source_system;
END
GO
