-- =========================================================
-- Payment System - Başlangıç verileri (EntryMode)
-- Idempotent: sadece henüz olmayan satırları ekler
-- =========================================================

USE PaymentSystem;
GO

MERGE INTO dbo.EntryMode AS target
USING (VALUES
    ('00', 'Unknown', 0),
    ('01', 'Manual', 0),
    ('02', 'MagneticStripe', 0),
    ('05', 'Chip', 0),
    ('07', 'ContactlessChip', 1),
    ('81', 'ECommerce', 0),
    ('90', 'MagneticStripeFullTrack', 0),
    ('91', 'ContactlessMagneticStripe', 1)
) AS source (Code, Name, IsContactless)
ON target.Code = source.Code
WHEN NOT MATCHED THEN
    INSERT (Code, Name, IsContactless) VALUES (source.Code, source.Name, source.IsContactless);
GO
