# Payment System

TCP üzerinden ISO8583 benzeri kart işlem mesajlarını alacak ödeme sisteminin veritabanı katmanı.

## Veritabanı (MS SQL Server, Docker)

### Kurulum
```bash
cp .env.example .env
# .env içindeki MSSQL_SA_PASSWORD değerini güçlü bir şifreyle değiştir
docker compose up -d
```

`db` servisi ayağa kalkıp sağlıklı (healthy) olduğunda, `db-init` servisi `docker/sql/init/` altındaki script'leri sırayla çalıştırıp `PaymentSystem` adlı veritabanını ve içindeki tabloları oluşturur (sistem `master` DB'si yerine ayrı bir DB kullanılır):

- `BankAccount`
- `Card`
- `TransactionType`
- `MtiProcessingCode`
- `DebitTransaction`

Script'ler idempotent'tir (`IF NOT EXISTS` / `MERGE`), yani container her yeniden başladığında tekrar çalışsa bile veriyi bozmaz.

### Kalıcılık
Veriler `payment_system_mssql_data` adlı Docker named volume'ünde tutulur. `docker compose down` / `docker compose up` ile veri kaybolmaz. Veriyi tamamen silmek için:
```bash
docker compose down -v
```

### Doğrulama
```bash
docker compose ps
docker exec -it payment_system_db /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P '<şifreniz>' -C -d PaymentSystem -Q "SELECT name FROM sys.tables"
```

### DBeaver / diğer istemciler
Bağlanırken Database alanına `PaymentSystem` yaz (varsayılan `master` değil) — tablolar orada.

## Tablo Şeması

| Tablo | Açıklama |
|---|---|
| `BankAccount` | Hesap no, hesap durumu, bakiye |
| `Card` | Kart bilgileri, ilişkili hesap |
| `TransactionType` | (Otc, Ots) kombinasyonundan işlem tipine lookup (Sale, MoneyTransfer, BalanceInquiry) |
| `MtiProcessingCode` | Gelen mesajın (Mti, F3_ProcessingCode) değerinden (Otc, Ots)'ye lookup — örn. `0200`+`000000` → Sale |
| `DebitTransaction` | ISO8583 alanlarını taşıyan işlem kayıtları — kolon adları `F2_CardNo`, `F3_ProcessingCode`, `F4_Amount`, `F49_CurrencyCode`, `F12_TransactionTime`, `F13_TransactionDate`, `F14_CardExpiry`, `F18_MerchantCode`, `F22_EntryMode`, `F39_ResponseCode`, `F43_Description` şeklinde ISO8583 field numarasını koruyor |

**Not:** Tablolar arasında foreign key yok — sadece database, tablo ve kolonlar oluşturuluyor. Sadece her tabloda kendi `PRIMARY KEY`'i var.

## Sıradaki Adımlar
- .NET tabanlı TCP sunucusu (ISO8583 parser, iş mantığı)
- EF Core ile bu şemaya bağlanma
