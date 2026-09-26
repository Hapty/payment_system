-- =========================================================
-- Payment System - TransactionType seed data
-- Idempotent: only inserts rows that don't already exist
-- =========================================================

MERGE INTO dbo.TransactionType AS target
USING (VALUES
    ('10', '11', 'Sale'),
    ('11', '11', 'MoneyTransfer'),
    ('40', '10', 'BalanceInquiry')
) AS source (Otc, Ots, Name)
ON target.Otc = source.Otc AND target.Ots = source.Ots
WHEN NOT MATCHED THEN
    INSERT (Otc, Ots, Name) VALUES (source.Otc, source.Ots, source.Name);
GO
