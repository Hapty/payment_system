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
    ('1005', 'Recipient card (F48) missing or invalid', '30'),
    ('1006', 'Transfer to the same account', '12'),
    ('1007', 'Merchant ID (F42) missing', '30'),
    ('2001', 'Card not found', '14'),
    ('2002', 'Card expired or F14 does not match', '54'),
    ('2003', 'Account not active', '62'),
    ('2004', 'Contactless not allowed for card', '57'),
    ('2005', 'Contactless limit exceeded', '65'),
    ('2006', 'CVV2 missing or does not match', 'N7'),
    ('2007', 'Recipient card not found', '14'),
    ('2008', 'Recipient account not active', '62'),
    ('3001', 'Amount is zero', '13'),
    ('3002', 'Insufficient funds', '51'),
    ('4001', 'Merchant not found', '03'),
    ('4002', 'Merchant not active', '03'),
    ('4003', 'Merchant type (F18) does not match merchant', '03'),
    ('9001', 'Exchange rate unavailable or stale', '91')
) AS source (Irc, Description, F39_ResponseCode)
ON target.Irc = source.Irc
WHEN NOT MATCHED THEN
    INSERT (Irc, Description, F39_ResponseCode) VALUES (source.Irc, source.Description, source.F39_ResponseCode);
GO
