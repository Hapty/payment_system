# Payment System

Kart işlemlerini (satış, bakiye sorgu) HTTP/JSON olarak alan ödeme sistemi. İşlemlerin tek girişi Gate'tir;
Gate isteği RabbitMQ kuyruğuyla Payment'a iletir ve cevabı bekler.

```
İstemci (Postman) ──HTTP / JSON──► Gate ──RabbitMQ (request-reply)──► Payment ──► SQL Server
                    POST :8080/api/transactions   payment.transactions     │
                                                                           └─ CRUD + kur: http://localhost:5002 (yönetim)
```

| Parça | Konum | Durum |
|---|---|---|
| Gate | `src/PaymentSystem.Gate` | `POST /api/transactions`: alanları ISO8583 tanımına göre doğrular, isteği kuyruğa yazar, Payment'ın cevabını (F39) döner |
| ISO8583 kütüphanesi | `src/PaymentSystem.Iso8583` | Mesaj modeli ve alan tanımları (Gate'in doğrulaması bunları kullanır); pack/unpack ve TCP framing |
| Payment | `src/PaymentSystem.Payment` | Kuyruktan işlem akışı (TL ve döviz satış, bakiye sorgu) + Web API ile 4 tablonun CRUD'u ve TCMB kur cache'i. Dapper |
| RabbitMQ | `docker-compose.yml` | Gate ↔ Payment arası kuyruk; yönetim arayüzü http://localhost:15672 |
| Veritabanı | `docker/sql` | Tablolar + seed |

## Veritabanı (MS SQL Server, Docker)

### Kurulum
```bash
cp .env.example .env
# .env içindeki MSSQL_SA_PASSWORD ve RABBITMQ_PASSWORD değerlerini güçlü şifrelerle değiştir
docker compose up -d
```

`db` servisi ayağa kalkıp sağlıklı (healthy) olduğunda, `db-init` servisi `docker/sql/init/` altındaki script'leri sırayla çalıştırıp `PaymentSystem` adlı veritabanını ve içindeki tabloları oluşturur (sistem `master` DB'si yerine ayrı bir DB kullanılır):

- `BankAccount`
- `Card`
- `TransactionType`
- `MtiProcessingCode`
- `DebitTransaction`
- `EntryMode`

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
| `EntryMode` | F22'nin ilk iki hanesinden giriş yöntemine lookup ve temassız olup olmadığı (`IsContactless`) — örn. `05` → Chip, `07` → ContactlessChip. 3. hane (PIN yeteneği) karara girmez |
| `DebitTransaction` | ISO8583 alanlarını taşıyan işlem kayıtları — kolon adları `F2_CardNo`, `F3_ProcessingCode`, `F4_Amount`, `F49_CurrencyCode`, `F12_TransactionTime`, `F13_TransactionDate`, `F14_CardExpiry`, `F18_MerchantCode`, `F22_EntryMode`, `F39_ResponseCode`, `F43_Description` şeklinde ISO8583 field numarasını koruyor |

**Not:** Tablolar arasında foreign key yok — sadece database, tablo ve kolonlar oluşturuluyor. Sadece her tabloda kendi `PRIMARY KEY`'i var.

## Gate (PaymentSystem.Gate)

.NET 9 Web API. İşlemlerin sisteme tek giriş noktası: `POST /api/transactions` (port `8080`). Örnek istekler: `src/PaymentSystem.Gate/PaymentSystem.Gate.http`.

Her istek için:
1. JSON gövdesi ISO8583 mesajına çevrilir ve her alan ISO8583 tanımına göre doğrulanır (aşağıdaki tablo). Bozuk ya da eksik alan → `400`; cevapta hangi alanda ne sorun olduğu yazar, istek Payment'a gitmez.
2. `0200` (finansal) isteği RabbitMQ'daki `payment.transactions` kuyruğuna yazılır ve Payment'ın cevabı beklenir (bkz. **Gate ↔ Payment: RabbitMQ**). Diğer MTI'lar: F39=`12`.
3. Cevap `200` ile döner; karar `responseCode` alanındadır (F39). Onay da red de (`51` yetersiz bakiye ...) `200`'dür.

```json
// istek
{ "mti": "0200", "cardNumber": "4111111111111111", "processingCode": "000000", "amountMinor": 15000, "currencyCode": "949",
  "transmissionDateTime": "0315095959", "stan": "000001", "localTime": "125959", "localDate": "0315", "terminalId": "TERM0001",
  "cardExpiry": "2812", "entryMode": "051" }
// cevap
{ "mti": "0210", "responseCode": "00", "stan": "000001", "rrn": null }
```

Gate işlem tipini (satış / bakiye / transfer) yorumlamaz; bu Payment'ın işi (`MtiProcessingCode` tablosu). Loglarda kart numarası maskelenir (`411111******1111`). Development modunda OpenAPI şeması `GET /openapi/v1.json`'dadır (Postman'a import edilebilir).

### Alanlar
| JSON | F | Format | Zorunlu (`0200`) |
|---|---|---|---|
| `mti` | — | 4 hane, istek MTI'ı (`0200`) | evet |
| `cardNumber` | 2 | rakam, 1–19 | evet |
| `processingCode` | 3 | n6 (`000000` satış, `310000` bakiye sorgu) | evet |
| `amountMinor` | 4 | sayı, kuruş cinsinden (150,00 → `15000`), en fazla 12 hane | evet |
| `transmissionDateTime` | 7 | n10 MMDDhhmmss | evet |
| `stan` | 11 | n6 | evet |
| `localTime` | 12 | n6 hhmmss | evet |
| `localDate` | 13 | n4 MMDD | evet |
| `terminalId` | 41 | ans8 | evet |
| `currencyCode` | 49 | n3 (949 = TRY, 840 = USD) | evet |
| `cardExpiry` | 14 | n4 YYMM | |
| `merchantType` | 18 | n4 | |
| `entryMode` | 22 | n3 (`051` çip, `071` temassız) | |
| `rrn` | 37 | an12 | |
| `merchantId` | 42 | ans15 | |
| `cardAcceptor` | 43 | ans40 (tam 40 karakter) | |

Alan tanımları `src/PaymentSystem.Iso8583/Iso87Fields.cs` içindedir.

### F39 (cevap kodu)
| Kod | Kim üretir | Anlamı |
|---|---|---|
| `12` | Gate | Desteklenmeyen MTI |
| `91` | Gate | Payment'tan `PaymentTimeoutSeconds` içinde cevap gelmedi, RabbitMQ'ya ulaşılamadı ya da cevap geçersiz |
| diğer | Payment | Olduğu gibi döner (örn. `00` onay, `51` yetersiz bakiye) |

Payment'ın ürettiği kodlar (`00`, `12`, `13`, `14`, `30`, `51`, `54`, `57`, `62`, `65`, `91`) için aşağıdaki **İşlem akışı** bölümüne bak.

### Ayarlar
`src/PaymentSystem.Gate/appsettings.json` içindeki `Gate` bölümü, ya da `Gate__RabbitMqUri` gibi env var'lar. RabbitMQ şifresi `RABBITMQ_PASSWORD` env var'ından gelir.

| Ayar | Varsayılan | |
|---|---|---|
| `RabbitMqUri` | `amqp://payment@localhost:5672` | Kuyruğun bulunduğu RabbitMQ (şifresiz) |
| `PaymentTimeoutSeconds` | `10` | Bu süre içinde cevap gelmezse F39=`91`; isteğin kuyruktaki ömrü de bu süre |

### Çalıştırma
```bash
# Docker (hepsi birlikte): http://localhost:8080
docker compose up -d --build
docker logs -f payment_system_gate

# lokal (RabbitMQ container'ı ayaktayken; gate container'ı durdurulmuş olmalı, ikisi de 8080'i kullanır)
$env:RABBITMQ_PASSWORD = "<şifreniz>"
dotnet run --project src/PaymentSystem.Gate --launch-profile http
```

## Gate ↔ Payment: RabbitMQ (request-reply)

İstemci için akış senkron kalır: Gate isteği kuyruğa yazar ve aynı HTTP isteği içinde cevabı bekler.

```
Gate ──publish──► payment.transactions ──► Payment (TransactionQueueConsumer → TransactionHandler → TransactionRepository)
  ▲   CorrelationId, ReplyTo, Expiration (TTL), Timestamp                 │
  └──────────────── amq.rabbitmq.reply-to (aynı CorrelationId) ◄──────────┘
```

- **Eşleştirme:** Her istek rastgele bir `CorrelationId` taşır; Payment cevabı isteğin `ReplyTo` adresine (RabbitMQ'nun direct reply-to'su, ayrı cevap kuyruğu açılmaz) aynı `CorrelationId` ile yazar. Aynı anda gelen istekler karışmaz.
- **Geç işlenme koruması:** Gate `PaymentTimeoutSeconds` dolunca `91` döner. İstek bu süreyi TTL (`Expiration`) olarak taşır: süresi dolan istek kuyruktan silinir; Payment da `Timestamp`'e bakıp kuyruktan almış olsa bile süresi dolmuş isteği işlemez. Böylece Payment sonradan ayağa kalksa bile istemcinin `91` aldığı işlemden para düşmez. Gate'e geç gelen cevap loglanıp atılır.
- **Tekrar:** Aynı istek ikinci kez gelirse UUID v5 idempotency anahtarı (bkz. İşlem akışı) para düşmesini engeller.
- **Kopma:** Gate RabbitMQ'ya ilk istekte bağlanır, bağlantı koparsa bir sonraki istekte yeniden bağlanır (o arada `91`). Payment bağlanamazsa 5 sn'de bir yeniden dener.
- **Paralellik:** Payment aynı anda en fazla 8 isteği işler (prefetch 8).
- Bilinen sınır: Payment isteği tam süre dolarken işlerse (işleme sırasında süre dolar) para düşer ama Gate `91` döner. Çözümü reversal (`0400`), sonraki adım.

Kuyruklar ve mesaj trafiği yönetim arayüzünden izlenebilir: http://localhost:15672 (kullanıcı `payment`, şifre `RABBITMQ_PASSWORD`).

## Payment (PaymentSystem.Payment)

.NET 9 Web API; veritabanına **Dapper** (düz SQL) ile erişir. Şema EF migration'larıyla değil `docker/sql/init/` script'leriyle yönetilir.
İşlemleri HTTP'den değil RabbitMQ kuyruğundan alır (bkz. **Gate ↔ Payment: RabbitMQ**). HTTP'de sadece yönetim için 4 tablonun CRUD endpoint'leri ve kur cache'i var.

| Kaynak | Route |
|---|---|
| BankAccount | `/api/bank-accounts`, `/api/bank-accounts/{accountNo}` |
| Card | `/api/cards`, `/api/cards/{id}`, `/api/cards/by-number/{cardNumber}` |
| TransactionType | `/api/transaction-types`, `/api/transaction-types/{otc}/{ots}` |
| MtiProcessingCode | `/api/mti-processing-codes`, `/api/mti-processing-codes/{mti}/{f3}` |
| Kur cache'i (salt okunur, sadece `GET`) | `/api/exchange-rates`, `/api/exchange-rates/{840 \| USD}` |

Katmanlar: **Controller** (sadece HTTP: sonucu 200/201/204/404/409'a çevirir) → **Handler** (`Handlers/`; iş kuralları ve `{Method}: ...` logları) → **Repository** (`Data/`; Dapper SQL, `I…Repository` arayüzleriyle). İşlem akışında controller yerine `TransactionQueueConsumer` → `TransactionHandler` → `TransactionRepository` vardır.

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

# lokal (DB ve RabbitMQ container'ları ayaktayken; payment container'ı durdurulmuş olmalı — ikisi de 5002'yi kullanır)
$env:MSSQL_SA_PASSWORD = "<şifreniz>"
$env:RABBITMQ_PASSWORD = "<şifreniz>"
dotnet run --project src/PaymentSystem.Payment --launch-profile http
```
Örnek istekler: `src/PaymentSystem.Payment/PaymentSystem.Payment.http`.

### İşlem akışı (`Transactions/`)
`TransactionQueueConsumer` kuyruktaki isteği (`TransactionRequest` JSON) alır, `TransactionHandler`'a verir ve `{ "responseCode": "00" }` cevabını Gate'e yazar. Teknik hatalarda da cevap verilir: okunamayan / geçersiz mesaj → `30`, DB'ye ulaşılamadı gibi beklenmeyen hata → `91` (transaction commit edilmediği için para hareketi yok).

1. `MtiProcessingCode` + `TransactionType` ile işlem tipi bulunur. Tanımsızsa `12` (kayıt açılmaz).
2. Tek SQL transaction'ında `DebitTransaction` kaydı açılır. **Idempotency:** kaydın `Guid`'i rastgele değil, isteğin `MTI + F2 + F3 + F4 + F13 + F12` alanlarından türetilen bir UUID v5'tir (şemaya kolon eklenmez). Aynı istek tekrar gelirse (aynı anda gelse bile) aynı Guid PK'ya takılır, ilk işlemin F39'u aynen döner, para ikinci kez düşmez. Sınır: tabloda STAN / terminal no olmadığı için aynı kartla aynı saniyede aynı tutarda iki gerçek işlem, ikincisi tekrar sayılır.
3. Kontroller, sırayla:

| Kontrol | F39 |
|---|---|
| F22 geldi ama ilk iki hanesi `EntryMode` tablosunda tanımlı değil (F22 yoksa temassız değil sayılır) | `12` |
| Kart yok | `14` |
| `Card.ExpiryDate` (MMYY) geçmiş ya da F14 (YYMM) kartla uyuşmuyor | `54` |
| Hesap yok ya da `AccountStatus` `Active` değil | `62` |
| **Satış:** para birimi için TCMB kuru yok (ör. `999`) | `12` |
| **Satış:** döviz ama cache'te kur yok ya da bayat (bkz. Döviz kurları) | `91` |
| **Satış:** tutar 0 | `13` |
| **Satış:** temassız (`EntryMode.IsContactless`, seed'de `07x`/`91x`) ama kart temassıza kapalı | `57` |
| **Satış:** temassız limit (TL karşılık üzerinden) aşıldı (terminal çip + PIN ile tekrar dener) | `65` |
| **Satış:** bakiye yetersiz | `51` |
| Onay — satışta bakiye düşer, `Card.LastTransaction*` güncellenir | `00` |

Limit, bakiye ve `Card.LastTransactionAmount` her zaman **TL karşılık** üzerinden yürür (hesaplar TL). Bakiye tek sorguda kontrol edilip düşülür (`... WHERE Balance >= @amount`), eşzamanlı satışlar hesabı eksiye düşüremez. Reddedilen işlemler de kendi F39'u ile kaydedilir. F13 (MMDD) yıl eklenerek `yyyymmdd` saklanır; yılbaşında gelen Aralık tarihi önceki yıla yazılır.

### Döviz kurları (`ExchangeRates/`)
```
TCMB today.xml ──(açılışta + her saat)──► ExchangeRateRefresher ──yazar──► IMemoryCache ("fx:USD" ...)
                                                                               │ sadece okur
İstemci → Gate → RabbitMQ → Payment / TransactionHandler: TL karşılık = tutar × döviz satış kuru (kuruşa, yarım yukarı)
```
- İşlem akışı TCMB'ye **hiç gitmez**, kuru sadece cache'ten okur; satışın hızı dış servise bağlı değildir.
- Kur: TCMB `ForexSelling` (döviz satış) / `Unit` (JPY gibi kurlar 100 birim için yayımlanır). F49'daki ISO 4217 sayısal kod TCMB'nin harf koduna çevrilir (`840` → USD, `978` → EUR ...).
- `DebitTransaction.F4_Amount` orijinal döviz tutarı, `LocalTxnAmount` TL karşılığıdır (TL işlemde ikisi aynı).
- Çekim başarısızsa (TCMB'ye ulaşılamadı, XML bozuk) cache'teki son kurlar kalır, `RetryMinutes` sonra tekrar denenir.
- Kur, son başarılı çekimden `MaxAgeHours` geçince bayat sayılır → `91`. Bülten tarihine bakılmaz: hafta sonu / bayramda TCMB bülten yayımlamaz, o günlerde son bülten resmî kurdur.
- Cache in-memory: Payment yeniden başlayınca boş açılır; ilk çekim bitene kadar (internet varsa 1 sn'nin altında) döviz satışları `91` alır. TL satışları etkilenmez.

Cache'in içeriği `GET /api/exchange-rates` (hepsi) ve `GET /api/exchange-rates/{kod}` (sayısal `840` ya da harf `USD`) ile görülür. Her kur için `sellingRate`, `bulletinDate` (TCMB bülten tarihi), `fetchedAt` (çekim zamanı, UTC) ve `isStale` (`true` ise işlem akışı bu kuru kullanmaz, `91` döner) gelir. Cache'te olmayan para biriminde `404`. Endpoint TCMB'ye gitmez, sadece cache'i okur.

Ayarlar (`appsettings.json` → `ExchangeRates`, ya da `ExchangeRates__RefreshMinutes` gibi ortam değişkenleri): `TcmbUrl`, `RefreshMinutes` (60), `RetryMinutes` (1), `MaxAgeHours` (24).

## Testler ve CI

| Test projesi | Kapsam | Gereken |
|---|---|---|
| `tests/PaymentSystem.Gate.Tests` | HTTP endpoint'in alan doğrulaması, Gate'in karar mantığı (sahte Payment ile), gerçek RabbitMQ üzerinden request-reply (sahte Payment cevaplayıcısıyla: eşzamanlı istekler, geçersiz cevap, timeout + TTL, geç cevap), ISO8583 pack/unpack ve framing | **Docker** |
| `tests/PaymentSystem.Payment.Tests` | İşlem akışı (idempotency, eşzamanlılık, tüm red kodları, döviz çevrimi), kuyruk tüketicisi (gerçek RabbitMQ + SQL: cevap, `30`, süresi dolmuş istek, DB yokken `91`), 4 CRUD controller + Dapper repository'leri, model doğrulama, TCMB ayrıştırma + kur cache'i + arka plan servisi (sahte HTTP ile) | **Docker** |

Testler [Testcontainers](https://dotnet.testcontainers.org/) ile gerçek SQL Server ve RabbitMQ container'ları başlatır (compose'dakiyle aynı imajlar). SQL Server'da `docker/sql/init/*.sql` script'leri çalıştırılır; yani SQL'ler compose'daki DB ile birebir aynı şemaya karşı test edilir. Testten önce Docker Desktop açık olmalı (GitHub Actions'ın ubuntu runner'ında Docker hazır).

```bash
dotnet test -p:CollectCoverage=true
```
Line coverage her test projesi için ayrı ayrı %80'in altındaysa komut hata verir (ayarlar test `.csproj`'larında; `Program.cs` coverage dışı). Rapor: `tests/*/TestResults/coverage.cobertura.xml`.

GitHub Actions (`.github/workflows/ci.yml`), `master`'a açılan her pull request'te ve `master`'a yapılan her push'ta restore → build → test + coverage kapısını çalıştırır; coverage özeti job summary'de görünür. `master` üzerindeki ruleset, `build-and-test` check'i geçmeden merge'e izin vermez.

## Sıradaki Adımlar
- Cevap sözleşmesini genişletmek: F38 (onay kodu), F37 (RRN), F54 (bakiye sorgusu cevabı).
- Timeout'ta reversal (`0400`): Gate cevap alamadığı işlem için kuyruğa iptal mesajı bırakır, Payment açıldığında işler (store & forward).
- Gate'e kimlik doğrulama (API key / mTLS); Payment'ın yönetim endpoint'lerini iç ağa almak.
- Asenkron işler (bildirim, mutabakat) için ayrı kuyruklar.
- DB şemasını ISO formatına uydurmak: `DebitTransaction.F13_TransactionDate` şu an `CHAR(8)` yyyymmdd (ISO: MMDD), `Card.ExpiryDate` MMYY (ISO F14: YYMM).
- Para transferi şu an `12` döner: alıcı kart numarası ISO mesajında / Gate sözleşmesinde henüz yok.
