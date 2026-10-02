-- =========================================================
-- Payment System - Tablo tanımları
-- Idempotent: tekrar çalıştırmak güvenli (oluşturmadan önce var mı diye bakar)
-- Foreign key yok - sadece tablolar ve kolonlar
-- =========================================================

IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = 'PaymentSystem')
BEGIN
    CREATE DATABASE PaymentSystem;
END
GO

USE PaymentSystem;
GO

-- 1) BankAccount
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'BankAccount')
BEGIN
    CREATE TABLE dbo.BankAccount
    (
        AccountNo       VARCHAR(34)     NOT NULL PRIMARY KEY,
        AccountStatus   VARCHAR(20)     NOT NULL,
        Balance         DECIMAL(18,2)   NOT NULL DEFAULT 0
    );
END
GO

-- 2) Card
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Card')
BEGIN
    CREATE TABLE dbo.Card
    (
        Id                      INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CardNumber              VARCHAR(19)     NOT NULL UNIQUE,
        AccountNo               VARCHAR(34)     NOT NULL,
        CardType                VARCHAR(20)     NULL,
        ExpiryDate              CHAR(4)         NULL,           -- MMYY
        Cvv                     CHAR(3)         NULL,
        LastTransactionDate     DATETIME2       NULL,
        LastTransactionAmount   DECIMAL(18,2)   NULL,
        ContactlessAllowed      BIT             NOT NULL DEFAULT 0,
        ContactlessLimit        DECIMAL(18,2)   NULL
    );
END
GO

-- 3) TransactionType (sözlük: otc/ots kombinasyonu -> işlem adı)
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TransactionType')
BEGIN
    CREATE TABLE dbo.TransactionType
    (
        Otc     CHAR(2)     NOT NULL,
        Ots     CHAR(2)     NOT NULL,
        Name    VARCHAR(50) NOT NULL,
        CONSTRAINT PK_TransactionType PRIMARY KEY (Otc, Ots)
    );
END
GO

-- 4) DebitTransaction
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'DebitTransaction')
BEGIN
    CREATE TABLE dbo.DebitTransaction
    (
        Guid                    UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
        CreatedAt               DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt               DATETIME2       NULL,
        Mti                     CHAR(4)         NOT NULL,
        F2_CardNo               VARCHAR(19)     NOT NULL,
        F3_ProcessingCode       CHAR(6)         NULL,
        Otc                     CHAR(2)         NOT NULL,
        Ots                     CHAR(2)         NOT NULL,
        F4_Amount               DECIMAL(18,2)   NOT NULL,
        F49_CurrencyCode        CHAR(3)         NULL,
        LocalTxnAmount          DECIMAL(18,2)   NULL,           -- kur x tutar
        F12_TransactionTime     CHAR(6)         NULL,           -- hhmmss
        F13_TransactionDate     CHAR(8)         NULL,           -- yyyymmdd
        F14_CardExpiry          CHAR(4)         NULL,
        F18_MerchantCode        VARCHAR(15)     NULL,
        F22_EntryMode           CHAR(3)         NULL,
        F39_ResponseCode        CHAR(2)         NULL,
        F43_Description         VARCHAR(100)    NULL,
        RecipientCardNumber     VARCHAR(19)     NULL            -- sadece para transferinde (kkpt)
    );
END
GO

-- 5) MtiProcessingCode (eşleme: MTI + F3 -> otc/ots)
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'MtiProcessingCode')
BEGIN
    CREATE TABLE dbo.MtiProcessingCode
    (
        Mti                 CHAR(4)     NOT NULL,
        F3_ProcessingCode   CHAR(6)     NOT NULL,
        Otc                 CHAR(2)     NOT NULL,
        Ots                 CHAR(2)     NOT NULL,
        CONSTRAINT PK_MtiProcessingCode PRIMARY KEY (Mti, F3_ProcessingCode)
    );
END
GO
