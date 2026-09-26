-- =========================================================
-- Payment System - Table Definitions
-- Idempotent: safe to re-run (checks existence before create)
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
        ContactlessLimit        DECIMAL(18,2)   NULL,
        CONSTRAINT FK_Card_BankAccount FOREIGN KEY (AccountNo)
            REFERENCES dbo.BankAccount (AccountNo)
    );
END
GO

-- 3) TransactionType (lookup: otc/ots combination -> meaning)
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
        CardNumber              VARCHAR(19)     NOT NULL,       -- f2
        ProcessingCode          CHAR(6)         NULL,           -- f3 (raw)
        Otc                     CHAR(2)         NOT NULL,
        Ots                     CHAR(2)         NOT NULL,
        Amount                  DECIMAL(18,2)   NOT NULL,       -- f4
        CurrencyCode            CHAR(3)         NULL,           -- f49
        LocalTxnAmount          DECIMAL(18,2)   NULL,           -- currency x amount
        TransactionTime         CHAR(6)         NULL,           -- f12 hhmmss
        TransactionDate         CHAR(8)         NULL,           -- f13 yyyymmdd
        CardExpiryFromTxn       CHAR(4)         NULL,           -- f14
        MerchantCode            VARCHAR(15)     NULL,           -- f18
        EntryMode               CHAR(3)         NULL,           -- f22
        ResponseCode            CHAR(2)         NULL,           -- f39
        Description             VARCHAR(100)    NULL,           -- f43
        RecipientCardNumber     VARCHAR(19)     NULL,           -- only for money transfer
        CONSTRAINT FK_DebitTransaction_Card FOREIGN KEY (CardNumber)
            REFERENCES dbo.Card (CardNumber),
        CONSTRAINT FK_DebitTransaction_RecipientCard FOREIGN KEY (RecipientCardNumber)
            REFERENCES dbo.Card (CardNumber),
        CONSTRAINT FK_DebitTransaction_TransactionType FOREIGN KEY (Otc, Ots)
            REFERENCES dbo.TransactionType (Otc, Ots)
    );
END
GO
