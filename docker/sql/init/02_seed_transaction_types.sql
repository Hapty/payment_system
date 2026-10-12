-- =========================================================
-- Payment System - Başlangıç verileri (TransactionType, MtiProcessingCode)
-- Idempotent: sadece henüz olmayan satırları ekler
-- =========================================================

USE PaymentSystem;
GO

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

MERGE INTO dbo.MtiProcessingCode AS target
USING (VALUES
    ('0200', '000000', '10', '11'),     -- Sale (satış)
    ('0200', '400000', '11', '11'),     -- MoneyTransfer (para transferi)
    ('0200', '310000', '40', '10')      -- BalanceInquiry (bakiye sorgulama)
) AS source (Mti, F3_ProcessingCode, Otc, Ots)
ON target.Mti = source.Mti AND target.F3_ProcessingCode = source.F3_ProcessingCode
WHEN NOT MATCHED THEN
    INSERT (Mti, F3_ProcessingCode, Otc, Ots)
    VALUES (source.Mti, source.F3_ProcessingCode, source.Otc, source.Ots);
GO
