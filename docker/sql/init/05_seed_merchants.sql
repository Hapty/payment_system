-- =========================================================
-- Payment System - Başlangıç verileri (Merchant)
-- Idempotent: sadece henüz olmayan satırları ekler
-- =========================================================

USE PaymentSystem;
GO

MERGE INTO dbo.Merchant AS target
USING (VALUES
    ('IYZICO',   'IYZICO',   '7399', 'Active'),     -- iş hizmetleri
    ('TRENDYOL', 'TRENDYOL', '5399', 'Active'),     -- genel mağaza
    ('GETIR',    'GETIR',    '5411', 'Active'),     -- market
    ('NETFLIX',  'NETFLIX',  '4899', 'Active')      -- yayın / abonelik
) AS source (MerchantId, Name, MerchantType, Status)
ON target.MerchantId = source.MerchantId
WHEN NOT MATCHED THEN
    INSERT (MerchantId, Name, MerchantType, Status) VALUES (source.MerchantId, source.Name, source.MerchantType, source.Status);
GO
