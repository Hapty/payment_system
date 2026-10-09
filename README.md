# Payment System

POS terminallerinden TCP üzerinden ISO8583 kart işlem mesajlarını alan ödeme sistemi.

```
POS ──TCP / ISO8583──► Gate ──HTTP / JSON──► Payment ──► SQL Server
                       :8583                 POST /api/transactions
```

| Parça | Konum | Durum |
|---|---|---|
| Gate | `src/PaymentSystem.Gate` | TCP'den mesajı okur, ISO8583'ü çözer, format kontrolü yapar, Payment'a iletir, cevabı (F39) ISO8583 olarak POS'a döner |
| ISO8583 kütüphanesi | `src/PaymentSystem.Iso8583` | Mesaj modeli, alan tanımları, pack/unpack, TCP framing (Gate ve simülatör ortak) |
| POS simülatörü | `tools/PosSimulator` | Gate'i denemek için mesaj üretip gönderen konsol uygulaması |
| Payment | `src/PaymentSystem.Payment` | Web API + Dapper. İşlem akışı (`POST /api/transactions`: TL ve döviz satış, bakiye sorgu) + 4 tablonun CRUD'u + TCMB kur cache'i |
| Veritabanı | `docker/sql` | Tablolar + seed |

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

## Gate (PaymentSystem.Gate)

.NET 9 worker servisi. TCP `8583` portunu dinler; bir POS bağlantıyı açık tutup art arda birçok mesaj gönderebilir.

Her mesaj için:
1. TCP'den çerçeveyi (frame) okur.
2. ISO8583'ü çözer ve alanların formatını kontrol eder.
3. MTI'ya göre karar verir:
   - `0800` (network / echo): Gate kendisi `0810`, F39=`00` döner; Payment'a gitmez.
   - `0200` (finansal): zorunlu alanlar (2, 3, 4, 7, 11, 12, 13, 41, 49) kontrol edilir, mesaj JSON'a çevrilip `POST {PaymentBaseUrl}/api/transactions` ile Payment'a gönderilir. Payment'ın `responseCode` değeri F39'a yazılır.
   - Diğer MTI'lar: F39=`12`.
4. Cevabı (`0210` / `0810` …) ISO8583 olarak POS'a geri yazar. POS'un cevabı isteğiyle eşleştirebilmesi için 2, 3, 4, 7, 11, 12, 13, 37, 41, 42, 49 alanları geri gönderilir.

Gate işlem tipini (satış / bakiye / transfer) yorumlamaz; bu Payment'ın işi (`MtiProcessingCode` tablosu). Loglarda kart numarası maskelenir (`411111******1111`).

### F39 (cevap kodu)
| Kod | Kim üretir | Anlamı |
|---|---|---|
| `00` | Gate (0800) / Payment | Onay |
| `12` | Gate | Desteklenmeyen MTI |
| `30` | Gate | Format hatası: bozuk ya da eksik alan |
| `91` | Gate | Payment'a ulaşılamadı: bağlantı hatası, timeout, 2xx olmayan ya da geçersiz cevap |
| diğer | Payment | Olduğu gibi POS'a iletilir (örn. `51` yetersiz bakiye) |

Payment'ın ürettiği kodlar (`00`, `12`, `13`, `14`, `51`, `54`, `57`, `62`, `65`) için aşağıdaki **İşlem akışı** bölümüne bak.

### Mesaj formatı (wire format)
```
[uzunluk: 2 bayt, big-endian][MTI: 4 ASCII][primary bitmap: 8 bayt][secondary bitmap: 8 bayt, bit 1 set ise][alanlar: ASCII, alan sırasıyla]
```
Uzunluk başlığı kendisini saymaz, en fazla 8192 bayt olabilir. Bitmap'te bit n (ilk baytın en soldaki biti = bit 1) "alan n mesajda var" demektir.

| F | Ad | Format |
|---|---|---|
| 2 | PAN | LLVAR n..19 (2 haneli uzunluk öneki) |
| 3 | Processing code | n6 |
| 4 | Tutar (kuruş: 150.00 → `000000015000`) | n12 |
| 7 | Transmission date/time MMDDhhmmss | n10 |
| 11 | STAN | n6 |
| 12 | Yerel saat hhmmss | n6 |
| 13 | Yerel tarih MMDD | n4 |
| 14 | Son kullanma YYMM | n4 |
| 18 | Merchant type | n4 |
| 22 | POS entry mode | n3 |
| 37 | RRN | an12 |
| 39 | Response code | an2 |
| 41 | Terminal ID | ans8 |
| 42 | Merchant ID | ans15 |
| 43 | İşyeri adı/konumu | ans40 |
| 49 | Para birimi (949 = TRY) | n3 |
| 70 | Network management code (secondary bitmap) | n3 |

Listede olmayan bir alan bitmap'te işaretliyse uzunluğu bilinemeyeceği için mesaj format hatası sayılır. Alan tanımları `src/PaymentSystem.Iso8583/Iso87Fields.cs` içindedir.

### Ayarlar
`src/PaymentSystem.Gate/appsettings.json` içindeki `Gate` bölümü, ya da `Gate__Port` gibi env var'lar:

| Ayar | Varsayılan | |
|---|---|---|
| `Port` | `8583` | POS'ların bağlandığı TCP portu |
| `PaymentBaseUrl` | `http://localhost:5002` | Payment servisinin adresi |
| `PaymentTimeoutSeconds` | `10` | Bu süre içinde cevap gelmezse F39=`91` |

### Çalıştırma
```bash
# lokal
dotnet run --project src/PaymentSystem.Gate

# ya da Docker (DB ile birlikte)
docker compose up -d --build

# sadece Gate (DB'siz)
docker compose up -d --build gate
docker logs -f payment_system_gate
```
Eski `payment-api` servisiyle çalışmış bir makinede ilk seferde `--remove-orphans` ekle: `docker compose up -d --build --remove-orphans`. Böylece artık compose dosyasında olmayan `payment_system_api` container'ı silinir.

Docker'da Gate, Payment'ı `http://payment:8080` adresinde arar. Sadece Gate ayaktaysa (Payment yoksa) logda `Name or service not known (payment:8080)` uyarısı görünür ve `0200` mesajları `91` alır.

### POS simülatörü
Gate çalışırken ayrı bir terminalde:
```bash
dotnet run --project tools/PosSimulator -- echo                          # 0800 → 0810 / 00
dotnet run --project tools/PosSimulator -- sale 4111111111111111 150.00  # 0200 → 0210 / 00 (kart ve bakiye varsa)
dotnet run --project tools/PosSimulator -- balance 4111111111111111
dotnet run --project tools/PosSimulator -- transfer 4111111111111111 25,50
dotnet run --project tools/PosSimulator -- sale 4111111111111111 10.00 --currency 840   # 10 USD, TL karşılığı düşer
# seçenekler: --host localhost --port 8583 --expiry 2812 (YYMM) --currency 949 (ISO 4217 sayısal kod)
```
Simülatör gönderdiği alanları, ham baytların hex dökümünü ve gelen cevabı alan alan yazdırır.

## Payment (PaymentSystem.Payment)

.NET 9 Web API; veritabanına **Dapper** (düz SQL) ile erişir. Şema EF migration'larıyla değil `docker/sql/init/` script'leriyle yönetilir.
Gate'in çağırdığı işlem akışı (`POST /api/transactions`) ve 4 tablonun CRUD endpoint'leri var.

| Kaynak | Route |
|---|---|
| BankAccount | `/api/bank-accounts`, `/api/bank-accounts/{accountNo}` |
| Card | `/api/cards`, `/api/cards/{id}`, `/api/cards/by-number/{cardNumber}` |
| TransactionType | `/api/transaction-types`, `/api/transaction-types/{otc}/{ots}` |
| MtiProcessingCode | `/api/mti-processing-codes`, `/api/mti-processing-codes/{mti}/{f3}` |

Her kaynakta `GET` (liste + tekil), `POST`, `PUT`, `DELETE`. Aynı anahtarla `POST` → `409`, geçersiz alan → `400`, bulunamayan kayıt → `404`.

Kurallar:
- Anahtar alanlar `PUT` ile değişmez (route'taki değer esas alınır). Card'ın anahtarı `Id` olduğu için kart numarası değiştirilebilir (başka kartta varsa `409`).
- `BankAccount.Balance` sadece `POST`'ta (başlangıç bakiyesi) verilir; `PUT` sadece `AccountStatus`'u değiştirir.
- `Card.LastTransactionDate` / `LastTransactionAmount` CRUD ile yazılamaz — işlem akışının alanları.
- `Card.Cvv` kolonu API'de hiç yok (PCI DSS: CVV saklanmamalı).
- Aynı anahtar kontrolü önce "var mı?" diye sorarak değil, DB'nin unique hatası (2627/2601) yakalanarak yapılır; eşzamanlı isteklerde de doğru `409` döner.

### Çalıştırma
```bash
# Docker (DB ile birlikte): http://localhost:5002
docker compose up -d --build

# lokal (DB container'ı ayaktayken; payment container'ı durdurulmuş olmalı — ikisi de 5002'yi kullanır)
$env:MSSQL_SA_PASSWORD = "<şifreniz>"
dotnet run --project src/PaymentSystem.Payment --launch-profile http
```
Örnek istekler: `src/PaymentSystem.Payment/PaymentSystem.Payment.http`.

### İşlem akışı (`POST /api/transactions`)
Gate'in gönderdiği `PaymentRequest` JSON'unu alır, `{ "responseCode": "00" }` döner. **İş sonucu (onay ya da red) her zaman `200`** ile döner; `4xx`/`5xx` sadece teknik hatalardır ve Gate onları `91`'e çevirir.

1. `MtiProcessingCode` + `TransactionType` ile işlem tipi bulunur. Tanımsızsa `12` (kayıt açılmaz).
2. Tek SQL transaction'ında `DebitTransaction` kaydı açılır. **Idempotency:** kaydın `Guid`'i rastgele değil, isteğin `MTI + F2 + F3 + F4 + F13 + F12` alanlarından türetilen bir UUID v5'tir (şemaya kolon eklenmez). Aynı istek tekrar gelirse (aynı anda gelse bile) aynı Guid PK'ya takılır, ilk işlemin F39'u aynen döner, para ikinci kez düşmez. Sınır: tabloda STAN / terminal no olmadığı için aynı kartla aynı saniyede aynı tutarda iki gerçek işlem, ikincisi tekrar sayılır.
3. Kontroller, sırayla:

| Kontrol | F39 |
|---|---|
| Kart yok | `14` |
| `Card.ExpiryDate` (MMYY) geçmiş ya da F14 (YYMM) kartla uyuşmuyor | `54` |
| Hesap yok ya da `AccountStatus` `Active` değil | `62` |
| **Satış:** para birimi için TCMB kuru yok (ör. `999`) | `12` |
| **Satış:** döviz ama cache'te kur yok ya da bayat (bkz. Döviz kurları) | `91` |
| **Satış:** tutar 0 | `13` |
| **Satış:** temassız (F22 `07x`/`91x`) ama kart temassıza kapalı | `57` |
| **Satış:** temassız limit (TL karşılık üzerinden) aşıldı (terminal çip + PIN ile tekrar dener) | `65` |
| **Satış:** bakiye yetersiz | `51` |
| Onay — satışta bakiye düşer, `Card.LastTransaction*` güncellenir | `00` |

Limit, bakiye ve `Card.LastTransactionAmount` her zaman **TL karşılık** üzerinden yürür (hesaplar TL). Bakiye tek sorguda kontrol edilip düşülür (`... WHERE Balance >= @amount`), eşzamanlı satışlar hesabı eksiye düşüremez. Reddedilen işlemler de kendi F39'u ile kaydedilir. F13 (MMDD) yıl eklenerek `yyyymmdd` saklanır; yılbaşında gelen Aralık tarihi önceki yıla yazılır.

### Döviz kurları (`ExchangeRates/`)
```
TCMB today.xml ──(açılışta + her saat)──► ExchangeRateRefresher ──yazar──► IMemoryCache ("fx:USD" ...)
                                                                               │ sadece okur
POS → Gate → Payment / TransactionProcessor: TL karşılık = tutar × döviz satış kuru (kuruşa, yarım yukarı)
```
- İşlem akışı TCMB'ye **hiç gitmez**, kuru sadece cache'ten okur; satışın hızı dış servise bağlı değildir.
- Kur: TCMB `ForexSelling` (döviz satış) / `Unit` (JPY gibi kurlar 100 birim için yayımlanır). F49'daki ISO 4217 sayısal kod TCMB'nin harf koduna çevrilir (`840` → USD, `978` → EUR ...).
- `DebitTransaction.F4_Amount` orijinal döviz tutarı, `LocalTxnAmount` TL karşılığıdır (TL işlemde ikisi aynı).
- Çekim başarısızsa (TCMB'ye ulaşılamadı, XML bozuk) cache'teki son kurlar kalır, `RetryMinutes` sonra tekrar denenir.
- Kur, son başarılı çekimden `MaxAgeHours` geçince bayat sayılır → `91`. Bülten tarihine bakılmaz: hafta sonu / bayramda TCMB bülten yayımlamaz, o günlerde son bülten resmî kurdur.
- Cache in-memory: Payment yeniden başlayınca boş açılır; ilk çekim bitene kadar (internet varsa 1 sn'nin altında) döviz satışları `91` alır. TL satışları etkilenmez.

Ayarlar (`appsettings.json` → `ExchangeRates`, ya da `ExchangeRates__RefreshMinutes` gibi ortam değişkenleri): `TcmbUrl`, `RefreshMinutes` (60), `RetryMinutes` (1), `MaxAgeHours` (24).

## Testler ve CI

| Test projesi | Kapsam | Gereken |
|---|---|---|
| `tests/PaymentSystem.Gate.Tests` | ISO8583 pack/unpack, TCP framing, Gate'in karar mantığı (sahte Payment ile), HTTP Payment client'ı, gerçek soket üzerinden TCP sunucusu | — |
| `tests/PaymentSystem.Payment.Tests` | İşlem akışı (idempotency, eşzamanlılık, tüm red kodları, döviz çevrimi), 4 CRUD controller + Dapper repository'leri, model doğrulama, TCMB ayrıştırma + kur cache'i + arka plan servisi (sahte HTTP ile) | **Docker** |

Payment testleri [Testcontainers](https://dotnet.testcontainers.org/) ile gerçek bir SQL Server container'ı başlatır ve içinde `docker/sql/init/*.sql` script'lerini çalıştırır; yani SQL'ler compose'daki DB ile birebir aynı şemaya karşı test edilir. Testten önce Docker Desktop açık olmalı (GitHub Actions'ın ubuntu runner'ında Docker hazır).

```bash
dotnet test -p:CollectCoverage=true
```
Line coverage her test projesi için ayrı ayrı %80'in altındaysa komut hata verir (ayarlar test `.csproj`'larında; `Program.cs` coverage dışı). Rapor: `tests/*/TestResults/coverage.cobertura.xml`.

GitHub Actions (`.github/workflows/ci.yml`), `master`'a açılan her pull request'te ve `master`'a yapılan her push'ta restore → build → test + coverage kapısını çalıştırır; coverage özeti job summary'de görünür. `master` üzerindeki ruleset, `build-and-test` check'i geçmeden merge'e izin vermez.

## Sıradaki Adımlar
- Cevap sözleşmesini genişletmek: F38 (onay kodu), F37 (RRN), F54 (bakiye sorgusu cevabı).
- Timeout'ta reversal (Gate → Payment), circuit breaker, `/api/transactions`'ı sadece Gate'e açmak.
- Gate ↔ Payment HTTP/JSON kalıyor; Kafka, Payment servisi bittikten sonra asenkron işler (bildirim, mutabakat) için.
- DB şemasını ISO formatına uydurmak: `DebitTransaction.F13_TransactionDate` şu an `CHAR(8)` yyyymmdd (ISO: MMDD), `Card.ExpiryDate` MMYY (ISO F14: YYMM).
- Para transferi şu an `12` döner: alıcı kart numarası ISO mesajında / Gate sözleşmesinde henüz yok.
