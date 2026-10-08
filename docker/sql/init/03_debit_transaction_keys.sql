-- =========================================================
-- Payment System - DebitTransaction: işlemi tekil olarak tanımlayan ISO8583 alanları
-- Idempotent: kolon/index zaten varsa dokunmaz (mevcut veritabanlarına da uygulanabilir)
-- =========================================================

-- sqlcmd varsayılan olarak QUOTED_IDENTIFIER OFF çalışır; filtrelenmiş (WHERE'li) index bunu ON ister.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

USE PaymentSystem;
GO

-- Kolonlar NULL olabilir: bu script'ten önce eklenmiş satırlar bu alanları taşımaz.
IF COL_LENGTH('dbo.DebitTransaction', 'F7_TransmissionDateTime') IS NULL
    ALTER TABLE dbo.DebitTransaction ADD F7_TransmissionDateTime CHAR(10) NULL;     -- MMDDhhmmss
IF COL_LENGTH('dbo.DebitTransaction', 'F11_Stan') IS NULL
    ALTER TABLE dbo.DebitTransaction ADD F11_Stan CHAR(6) NULL;
IF COL_LENGTH('dbo.DebitTransaction', 'F37_Rrn') IS NULL
    ALTER TABLE dbo.DebitTransaction ADD F37_Rrn VARCHAR(12) NULL;
IF COL_LENGTH('dbo.DebitTransaction', 'F41_TerminalId') IS NULL
    ALTER TABLE dbo.DebitTransaction ADD F41_TerminalId VARCHAR(8) NULL;
IF COL_LENGTH('dbo.DebitTransaction', 'F42_MerchantId') IS NULL
    ALTER TABLE dbo.DebitTransaction ADD F42_MerchantId VARCHAR(15) NULL;
GO

-- Idempotency anahtarı: aynı terminalden aynı STAN + F7 ile gelen istek aynı işlemdir (POS/Gate tekrar göndermiş).
-- Payment ikinci kaydı eklemeye çalışınca bu index hata verir ve ilk işlemin cevabı tekrar döner; para iki kez düşmez.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_DebitTransaction_TerminalStanF7')
    CREATE UNIQUE INDEX UX_DebitTransaction_TerminalStanF7
        ON dbo.DebitTransaction (F41_TerminalId, F11_Stan, F7_TransmissionDateTime)
        WHERE F41_TerminalId IS NOT NULL AND F11_Stan IS NOT NULL AND F7_TransmissionDateTime IS NOT NULL;
GO
