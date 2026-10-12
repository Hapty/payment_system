-- =========================================================
-- Payment System - Başlangıç verileri (InternalResponseCode)
-- Idempotent: sadece henüz olmayan satırları ekler
-- =========================================================

USE PaymentSystem;
GO

MERGE INTO dbo.InternalResponseCode AS target
USING (VALUES
    ('0000', 'Approved', '00'),
    ('1001', 'Transaction type not defined (MTI + F3)', '12'),
    ('1002', 'Entry mode (F22) not defined', '12'),
    ('1003', 'Transaction type not supported', '12'),
    ('1004', 'Currency not supported', '12'),
    ('2001', 'Card not found', '14'),
    ('2002', 'Card expired or F14 does not match', '54'),
    ('2003', 'Account not active', '62'),
    ('2004', 'Contactless not allowed for card', '57'),
    ('2005', 'Contactless limit exceeded', '65'),
    ('3001', 'Amount is zero', '13'),
    ('3002', 'Insufficient funds', '51'),
    ('9001', 'Exchange rate unavailable or stale', '91')
) AS source (Irc, Description, F39_ResponseCode)
ON target.Irc = source.Irc
WHEN NOT MATCHED THEN
    INSERT (Irc, Description, F39_ResponseCode) VALUES (source.Irc, source.Description, source.F39_ResponseCode);
GO
