# Payment System

TCP üzerinden ISO8583 benzeri kart işlem mesajlarını alacak ödeme sisteminin veritabanı katmanı.

## Veritabanı (MS SQL Server, Docker)

### Kurulum
```bash
cp .env.example .env
# .env içindeki MSSQL_SA_PASSWORD değerini güçlü bir şifreyle değiştir
docker compose up -d
```

`db` servisi ayağa kalkıp sağlıklı (healthy) olduğunda, `db-init` servisi `docker/sql/init/` altındaki script'leri sırayla çalıştırıp tabloları oluşturur:

- `BankAccount`
- `Card`
- `TransactionType`
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
docker exec -it payment_system_db /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P '<şifreniz>' -C -Q "SELECT name FROM sys.tables"
```

## Tablo Şeması

| Tablo | Açıklama |
|---|---|
| `BankAccount` | Hesap no, hesap durumu, bakiye |
| `Card` | Kart bilgileri, ilişkili hesap |
| `TransactionType` | (Otc, Ots) kombinasyonundan işlem tipine lookup (Sale, MoneyTransfer, BalanceInquiry) |
| `DebitTransaction` | ISO8583 alanlarından (f2, f3, f4, f12, f13, f14, f18, f22, f39, f43, f49) türetilen işlem kayıtları |

## Sıradaki Adımlar
- .NET tabanlı TCP sunucusu (ISO8583 parser, iş mantığı)
- EF Core ile bu şemaya bağlanma
