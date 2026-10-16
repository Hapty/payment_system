# Payment System

Kart işlemlerini (satış, bakiye sorgu) HTTP/JSON olarak alan ödeme sistemi. İşlemlerin tek girişi Gate'tir;
Gate isteği RabbitMQ kuyruğuyla Payment'a iletir ve cevabı bekler. Payment her kararı Kafka'ya olay olarak yazar;
Notification bu olaylardan onaylı satışları müşteriye e-postayla bildirir (asenkron, işlemi bekletmez).

```
İstemci (Postman) ──HTTP / JSON──► Gate ──RabbitMQ (request-reply)──► Payment ──► SQL Server
                    POST :8080/api/transactions   payment.transactions     │  │
                                                                           │  └─ CRUD + kur: http://localhost:5002 (yönetim)
                                                                           │
                                        Kafka: payment.transaction-events ◄┘ (her karar, bloklamadan)
                                                       │
                                                       ▼
                                                 Notification ──SMTP (Gmail)──► müşterinin e-postası (onaylı satış)
```

| Parça | Konum | Durum |
|---|---|---|
| Gate | `src/PaymentSystem.Gate` | `POST /api/transactions`: alanları ISO8583 tanımına göre doğrular, isteği kuyruğa yazar, Payment'ın cevabını (F39) döner |
| ISO8583 kütüphanesi | `src/PaymentSystem.Iso8583` | Mesaj modeli ve alan tanımları (Gate'in doğrulaması bunları kullanır); pack/unpack ve TCP framing |
| Payment | `src/PaymentSystem.Payment` | Kuyruktan işlem akışı (TL ve döviz satış, bakiye sorgu) + Web API ile 4 tablonun CRUD'u ve TCMB kur cache'i. Dapper |
| Notification | `src/PaymentSystem.Notification` | Kafka'daki işlem olaylarını okur, onaylı satışlarda hesabın e-postasına SMTP ile bildirim atar |
| RabbitMQ | `docker-compose.yml` | Gate ↔ Payment arası kuyruk; yönetim arayüzü http://localhost:15672 |
| Kafka | `docker-compose.yml` | Payment'ın işlem olayları (`payment.transaction-events`); KRaft, tek node |
| Veritabanı | `docker/sql` | Tablolar + seed |

## Veritabanı (MS SQL Server, Docker)

### Kurulum
```bash
cp .env.example .env
# .env içindeki MSSQL_SA_PASSWORD ve RABBITMQ_PASSWORD değerlerini güçlü şifrelerle değiştir,
# e-posta bildirimleri için SMTP_USER ve SMTP_PASSWORD'ü doldur (bkz. Notification)
docker compose up -d
```

`db` servisi ayağa kalkıp sağlıklı (healthy) olduğunda, `db-init` servisi `docker/sql/init/` altındaki script'leri sırayla çalıştırıp `PaymentSystem` adlı veritabanını ve içindeki tabloları oluşturur (sistem `master` DB'si yerine ayrı bir DB kullanılır):

- `BankAccount`
- `Card`
- `TransactionType`
- `MtiProcessingCode`
- `DebitTransaction`
- `EntryMode`
- `InternalResponseCode`

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
| `BankAccount` | Hesap no, hesap durumu, bakiye, e-posta (opsiyonel; bildirimler buraya gider) |
| `Card` | Kart bilgileri, ilişkili hesap |
| `TransactionType` | (Otc, Ots) kombinasyonundan işlem tipine lookup (Sale, MoneyTransfer, BalanceInquiry) |
| `MtiProcessingCode` | Gelen mesajın (Mti, F3_ProcessingCode) değerinden (Otc, Ots)'ye lookup — örn. `0200`+`000000` → Sale |
| `InternalResponseCode` | İç karar kodundan (IRC) POS'a dönen F39'a lookup — örn. `3002` (bakiye yetersiz) → `51`. Birkaç IRC aynı F39'a çıkabilir |
| `EntryMode` | F22'nin ilk iki hanesinden giriş yöntemine lookup ve temassız olup olmadığı (`IsContactless`) — örn. `05` → Chip, `07` → ContactlessChip. 3. hane (PIN yeteneği) karara girmez |
| `DebitTransaction` | ISO8583 alanlarını taşıyan işlem kayıtları — kolon adları `F2_CardNo`, `F3_ProcessingCode`, `F4_Amount`, `F49_CurrencyCode`, `F12_TransactionTime`, `F13_TransactionDate`, `F14_CardExpiry`, `F18_MerchantCode`, `F22_EntryMode`, `F39_ResponseCode`, `F43_Description` şeklinde ISO8583 field numarasını koruyor |

**Not:** Tablolar arasında foreign key yok — sadece database, tablo ve kolonlar oluşturuluyor. Sadece her tabloda kendi `PRIMARY KEY`'i var.

## Gate (PaymentSystem.Gate)

.NET 9 Web API. İşlemlerin sisteme tek giriş noktası: `POST /api/transactions` (port `8080`). Örnek istekler: `src/PaymentSystem.Gate/PaymentSystem.Gate.http`.

Her istek için:
1. JSON gövdesinde alanlar ISO8583 alan numarasıyla gelir (`"F2"`, `"F3"` ...). Gövde ISO8583 mesajına çevrilir ve her alan ISO8583 tanımına göre doğrulanır (aşağıdaki tablo). Bozuk ya da eksik alan → `400`; cevapta hangi alanda ne sorun olduğu yazar, istek Payment'a gitmez.
2. `0200` (finansal) isteği RabbitMQ'daki `payment.transactions` kuyruğuna yazılır ve Payment'ın cevabı beklenir (bkz. **Gate ↔ Payment: RabbitMQ**). Diğer MTI'lar: F39=`12`.
3. Cevap `200` ile döner; karar `F39` alanındadır. Onay da red de (`51` yetersiz bakiye ...) `200`'dür.

```json
// istek
{ "mti": "0200", "F2": "4111111111111111", "F3": "000000", "F4": 15000, "F49": "949",
  "F7": "0315095959", "F11": "000001", "F12": "125959", "F13": "20260315", "F41": "TERM0001",
  "F14": "2812", "F22": "051" }
// cevap
{ "mti": "0210", "F39": "00", "F11": "000001", "F37": null }
```

Gate işlem tipini (satış / bakiye / transfer) yorumlamaz; bu Payment'ın işi (`MtiProcessingCode` tablosu). Loglarda alanlar Gate'te de Payment'ta da `F2 (Kart No)=411111********11` biçiminde, numarası ve ne olduğuyla yazılır (etiketler `Iso87Fields`'ta, biçim `IsoLog`'da); kart numaraları (F2, F48) maskelenir, CVV sadece `cvv=***` olarak görünür. Development modunda OpenAPI şeması `GET /openapi/v1.json`'dadır (Postman'a import edilebilir).

### Alanlar
| JSON | Alan | Format | Zorunlu (`0200`) |
|---|---|---|---|
| `mti` | — | 4 hane, istek MTI'ı (`0200`) | evet |
| `F2` | kart no (PAN) | rakam, 1–19 | evet |
| `F3` | işlem kodu | n6 (`000000` satış, `310000` bakiye sorgu, `400000` para transferi) | evet |
| `F4` | tutar | sayı, kuruş cinsinden (150,00 → `15000`), en fazla 12 hane | evet |
| `F7` | gönderim zamanı | n10 MMDDhhmmss | evet |
| `F11` | STAN | n6 | evet |
| `F12` | yerel saat | n6 hhmmss | evet |
| `F13` | yerel tarih | **n8 yyyymmdd** (gerçek bir tarih olmalı) | evet |
| `F41` | terminal no | ans8 | evet |
| `F49` | para birimi | n3 (949 = TRY, 840 = USD) | evet |
| `F14` | son kullanma | n4 YYMM | |
| `F18` | işyeri tipi | n4 | |
| `F22` | giriş yöntemi | n3 (`051` çip, `071` temassız, `812` e-ticaret) | |
| `F37` | RRN | an12 | |
| `F42` | işyeri no | ans15 | |
| `F43` | işyeri adı / yeri | ans40 (tam 40 karakter) | |
| `F48` | ek veri | ans..999 (LLLVAR); bu sistemde **alıcı kart no** | para transferinde |
| `cvv` | — | 3 ya da 4 rakam (CVV2, kartın arkası) | elle girişte (`01x`) ve e-ticarette (`81x`) |

Alan tanımları `src/PaymentSystem.Iso8583/Iso87Fields.cs` içindedir. **F13** ISO8583'te n4 MMDD'dir: Gate JSON'da yıllı halini (`yyyymmdd`) alır, ISO mesajına MMDD olarak koyar, yıllı halini Payment'a ayrıca iletir (Payment tarihi yıl tahmin etmeden olduğu gibi saklar). `cvv` bir ISO8583:1987 alanı değildir (gerçek ağlarda markaya özel alt alanda taşınır, örn. Visa F126.10); Gate onu ISO mesajının dışında, ayrı alan olarak Payment'a iletir ve hiçbir yere loglamaz.

### F39 (cevap kodu)
| Kod | Kim üretir | Anlamı |
|---|---|---|
| `12` | Gate | Desteklenmeyen MTI |
| `91` | Gate | Payment'tan `PaymentTimeoutSeconds` içinde cevap gelmedi, RabbitMQ'ya ulaşılamadı ya da cevap geçersiz |
| diğer | Payment | Olduğu gibi döner (örn. `00` onay, `51` yetersiz bakiye) |

Payment'ın ürettiği kodlar (`00`, `12`, `13`, `14`, `30`, `51`, `54`, `57`, `62`, `65`, `91`, `N7`) için aşağıdaki **İşlem akışı** bölümüne bak.

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
- Anahtar alanlar `PUT` ile değişmez (route'taki değer esas alınır). Kartın numarası da değiştirilemez: farklı numara gönderilirse `400` (CVV / PIN hash'i numaraya bağlı; yeni numara için yeni kart oluşturulur).
- `BankAccount.Balance` sadece `POST`'ta (başlangıç bakiyesi) verilir; `PUT` sadece `AccountStatus` ve `Email`'i değiştirir (`email` gönderilmezse silinir).
- `BankAccount.Email` opsiyoneldir; doluysa onaylı satışlarda Notification servisi bu adrese e-posta atar (bkz. **Notification**).
- `Card.LastTransactionDate` / `LastTransactionAmount` CRUD ile yazılamaz — işlem akışının alanları.
- `Card` tablosunda CVV / PIN kolonu yok; hash'leri `CardSecret` tablosundadır, bkz. **CVV2 ve PIN**.
- `POST /api/cards` cevabında kartın CVV2'si (`cvv`, 3 hane) ve PIN'i (`pin`, 4 hane) **bir kez** döner: kartın arkasına basılan değer ve PIN zarfının karşılığı. Sonradan hiçbir endpoint göstermez; not alınmalı.
- Aynı anahtar kontrolü önce "var mı?" diye sorarak değil, DB'nin unique hatası (2627/2601) yakalanarak yapılır; eşzamanlı isteklerde de doğru `409` döner.

### Çalıştırma
```bash
# Docker (DB ile birlikte): http://localhost:5002
docker compose up -d --build

# lokal (DB, RabbitMQ ve Kafka container'ları ayaktayken; payment container'ı durdurulmuş olmalı — ikisi de 5002'yi kullanır)
$env:MSSQL_SA_PASSWORD = "<şifreniz>"
$env:RABBITMQ_PASSWORD = "<şifreniz>"
$env:CARD_SECRET_KEY = "<64 hex>"
dotnet run --project src/PaymentSystem.Payment --launch-profile http
```
Örnek istekler: `src/PaymentSystem.Payment/PaymentSystem.Payment.http`.

### İşlem akışı (`Transactions/`)
`TransactionQueueConsumer` kuyruktaki isteği (`TransactionRequest` JSON) alır, `TransactionHandler`'a verir ve `{ "responseCode": "00" }` cevabını Gate'e yazar. Teknik hatalarda da cevap verilir: okunamayan / geçersiz mesaj → `30`, DB'ye ulaşılamadı gibi beklenmeyen hata → `91` (transaction commit edilmediği için para hareketi yok).

Onaylanmayan her işlemde `TransactionHandler` bir `TransactionException` fırlatır. Bu exception iş reddinde de (`51`, `14`, `57` ...) teknik hatada da (`91`, `96`) şu alanları taşır:

| Alan | İçerik |
|---|---|
| `ResponseCode` / `Irc` | F39 ve iç kod (teknik hatada ve tekrar gelen istekte IRC yok) |
| `Description` | `InternalResponseCode.Description` ya da teknik hatanın açıklaması |
| `CardNumber` | **Maskeli** (`411111********11`) |
| `TransactionId` | `DebitTransaction.Guid` (işlem tipi tanımsızsa kayıt açılmadığı için boş) |
| `AccountNo` | Kartın hesabı (kart bulunamadıysa boş) |

Red kaydı F39'u ile commit edildikten sonra fırlatılır, kayıt geri alınmaz. `TransactionQueueConsumer` exception'ı yakalar: iş reddini uyarı, teknik hatayı hata olarak loglar ve Gate'e sadece F39'u döner (`{ "responseCode": "51" }`).

1. `MtiProcessingCode` + `TransactionType` ile işlem tipi bulunur (tanım cache'inden, bkz. Tanım tabloları cache'i). Tanımsızsa IRC `1001` → `12` (kayıt açılmaz).
2. Tek SQL transaction'ında `DebitTransaction` kaydı açılır. **Idempotency:** kaydın `Guid`'i rastgele değil, isteğin `MTI + F2 + F3 + F4 + F13 + F12` alanlarından türetilen bir UUID v5'tir (şemaya kolon eklenmez). Aynı istek tekrar gelirse (aynı anda gelse bile) aynı Guid PK'ya takılır, ilk işlemin F39'u aynen döner, para ikinci kez düşmez. Sınır: tabloda STAN / terminal no olmadığı için aynı kartla aynı saniyede aynı tutarda iki gerçek işlem, ikincisi tekrar sayılır.
3. Kontroller, sırayla. Her sebep bir iç kod (IRC) üretir; POS'a dönen F39 `InternalResponseCode` tablosundan okunur (aşağıdaki F39'lar seed değerleridir). IRC tabloda yoksa hata loglanır ve `96` döner. Teknik hatalar (`30`, `91`) DB'ye bağlı olmadığı için kodda sabittir.

| Kontrol | IRC | F39 |
|---|---|---|
| F22 geldi ama ilk iki hanesi `EntryMode` tablosunda tanımlı değil (F22 yoksa temassız değil sayılır) | `1002` | `12` |
| Kart yok | `2001` | `14` |
| `Card.ExpiryDate` (MMYY) geçmiş ya da F14 (YYMM) kartla uyuşmuyor | `2002` | `54` |
| Kart fiziksel olarak yok (F22 `01x` elle giriş / `81x` e-ticaret) ama CVV2 gelmedi; ya da (hangi girişte olursa olsun) gelen CVV2 tutmuyor | `2006` | `N7` |
| Hesap yok ya da `AccountStatus` `Active` değil | `2003` | `62` |
| **Transfer:** F48 yok ya da kart numarası değil (1–19 rakam) | `1005` | `30` |
| **Transfer:** alıcı kart (F48) yok | `2007` | `14` |
| **Transfer:** alıcı kart gönderenle aynı hesapta (aynı kart dahil) | `1006` | `12` |
| **Transfer:** alıcı kartın hesabı yok ya da `Active` değil | `2008` | `62` |
| **Satış / transfer:** para birimi için TCMB kuru yok (ör. `999`) | `1004` | `12` |
| **Satış / transfer:** döviz ama cache'te kur yok ya da bayat (bkz. Döviz kurları) | `9001` | `91` |
| **Satış / transfer:** tutar 0 | `3001` | `13` |
| **Satış:** temassız (`EntryMode.IsContactless`, seed'de `07x`/`91x`) ama kart temassıza kapalı | `2004` | `57` |
| **Satış:** temassız limit (TL karşılık üzerinden) aşıldı (terminal çip + PIN ile tekrar dener) | `2005` | `65` |
| **Satış / transfer:** bakiye yetersiz | `3002` | `51` |
| Onay — satışta bakiye düşer; transferde gönderenden düşer, alıcıya eklenir; gönderen kartın `Card.LastTransaction*`'ı güncellenir | `0000` | `00` |

Limit, bakiye ve `Card.LastTransactionAmount` her zaman **TL karşılık** üzerinden yürür (hesaplar TL). Bakiye tek sorguda kontrol edilip düşülür (`... WHERE Balance >= @amount`), eşzamanlı satışlar hesabı eksiye düşüremez. Reddedilen işlemler de kendi F39'u ile kaydedilir. F13 Gate'ten `yyyymmdd` gelir ve olduğu gibi saklanır.

### Karttan karta para transferi (`0200` + F3 `400000`)
Sadece bu sistemdeki kartlar arasında (on-us). Alıcı kart numarası **F48**'de gelir.
- Gönderen kart için satıştaki ortak kontroller aynen yapılır: F22, kart, son kullanma (F14), CVV2 (elle giriş / e-ticarette zorunlu), hesap aktif mi.
- Alıcı kart bulunmalı, gönderenle aynı hesapta olmamalı, hesabı `Active` olmalı. Alıcı kartın son kullanma tarihine bakılmaz.
- Döviz de olabilir: iki hesap da TL olduğu için **TL karşılığı** (satıştaki kurla) gönderenden düşülür, alıcıya eklenir. Temassız limit uygulanmaz.
- Düşme ve ekleme, `DebitTransaction` kaydıyla aynı SQL transaction'ındadır: ya ikisi birden olur ya hiçbiri. İki hesabın satırı önce PK sırasıyla kilitlenir; A→B ve B→A transferleri aynı anda gelse de deadlock olmaz.
- Kayıt tek satırdır: gönderen `F2_CardNo`'da, alıcı `RecipientCardNumber`'da (reddedilen transferde de yazılır).

### CVV2 ve PIN (`Cards/`, `CardSecret` tablosu)
Kart oluşturulurken CVV2 (3 hane) ve PIN (4 hane) **rastgele** üretilir (`RandomNumberGenerator`). Düz halleri sadece `POST` cevabında bir kez döner; DB'ye `CardSecret` tablosuna **hash'leri** yazılır. `Card` ve `CardSecret` aynı SQL transaction'ında yazılır, kart silinince `CardSecret` satırı da silinir (`ON DELETE CASCADE`).

```
Kart oluşturulurken:  rastgele 123 / 4821 ──HMAC-SHA256(CARD_SECRET_KEY, PAN:cvv:123 / PAN:pin:4821)──► CardSecret.CvvHash / PinHash
Satış sırasında:      gelen cvv        ──HMAC-SHA256(CARD_SECRET_KEY, PAN:cvv:<gelen>)──────────────► == CardSecret.CvvHash ?  hayır → N7
```

- **Neden anahtarlı hash (HMAC):** CVV'nin 1000, PIN'in 10.000 olası değeri var; düz SHA-256 olsaydı DB'yi ele geçiren hepsini saniyeler içinde dener. Anahtar DB'de değil ortam değişkenindedir, DB tek başına sızarsa hash'ler işe yaramaz. Hash geri çözülemez; doğrulamada gelen değer aynı şekilde hash'lenip karşılaştırılır (sabit zamanlı).
- **Kart numarası hash'e girer:** aynı CVV'li iki kartın hash'i farklıdır. Bu yüzden kart numarası `PUT` ile değiştirilemez (`400`).
- **Ne zaman kontrol edilir (CVV2):** kart fiziksel olarak yokken zorunlu: elle giriş (F22 `01x`) ve e-ticaret (`81x`). Çipli / temassız işlemde CVV istenmez (kart kendisi kriptogram üretir); ama yine de geldiyse doğrulanır.
- **PIN** şimdilik sadece saklanır; işlemlerde doğrulanmaz (ileride F52 PIN block ile).
- **Anahtar:** `CARD_SECRET_KEY` ortam değişkeni (`.env`), 64 hex = 32 bayt. Anahtar değişirse **tüm kartların CVV / PIN hash'i geçersiz olur**.
- **Gerçek bankalarda** anahtar HSM'in içindedir ve dışarı çıkmaz. Hash `ICardSecretHasher` arayüzünün arkasında: Vault / Azure Key Vault / AWS Payment Cryptography gibi bir hizmete geçmek kontrol mantığını değiştirmez.
- Düz CVV / PIN hiçbir yere yazılmaz: DB, loglar, Kafka olayı.
- `CardSecret` tablosundan önce oluşturulan kartların satırı yoktur; onlarla elle giriş / e-ticaret yapılamaz (`N7`), çip / temassız işlemler etkilenmez. Yeni kart oluşturulmalı.

### Tanım tabloları cache'i (`Parameters/`)
`TransactionType` + `MtiProcessingCode` (işlem tipi yönlendirmesi) , `EntryMode` (F22) ve `InternalResponseCode` (IRC → F39) işlem başına DB'den okunmaz: `ParameterCache` dört tabloyu tek seferde `IMemoryCache`'e yükler, tanımsız bir kod da DB'ye gitmeden tanımsız sayılır.

- **Yenileme:** yüklendikten `Parameters:RefreshMinutes` (varsayılan **10** dk; ortam değişkeni `Parameters__RefreshMinutes`) sonra ilk işlemde yeniden okunur.
- **CRUD:** `/api/transaction-types` veya `/api/mti-processing-codes` ile yapılan başarılı bir ekleme / güncelleme / silme cache'i hemen boşaltır; sonraki işlem yeni tanımı görür.
- **EntryMode / InternalResponseCode:** API'leri yok, doğrudan SQL ile değiştirilir; değişiklik en geç 10 dk içinde devreye girer.
- **Sınır:** birden fazla Payment instance'ı çalışırsa CRUD sadece isteği alan instance'ın cache'ini boşaltır, diğerleri değişikliği en geç 10 dk içinde görür.

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

### İşlem olayları (`Events/`)
Commit edilen her karar (onay da red de) Kafka'daki `payment.transaction-events` topic'ine bir `TransactionCompleted` olayı olarak yazılır. Topic tek bir servise değil, "Payment'ta ne oldu" sorusuna aittir: her tüketici (bugün Notification; yarın fraud, raporlama ...) kendi consumer group'uyla okur ve ilgilendiği olayları kendisi seçer. Yeni tüketici eklemek için Payment'a dokunmak gerekmez.

- **Bloklamaz:** olay kütüphanenin yerel kuyruğuna bırakılır, Gate'e dönen cevap Kafka'yı beklemez. Kafka kapalıysa olay `MessageTimeoutMs` (30 sn) boyunca denenir, sonra loglanıp bırakılır; işlemin kendisi etkilenmez (outbox yok, bilinçli tercih).
- **Olmayanlar:** tekrar gelen istek (ilk istek olayını zaten yazdı), kayıt açılmayan red (tanımsız işlem tipi, `12`) ve teknik hata (`91`) olay üretmez.
- **Anahtar:** hesap no; aynı hesabın olayları aynı partition'a düşer, sırası korunur (kart bulunamadıysa anahtar boş).
- **PCI:** kart numarası sadece maskeli (`411111********11`) yazılır.
- **Sürüm:** `eventType` (`TransactionCompleted`) ve `version` (`1`). Alan eklemek geriye uyumludur; alan silmek / anlamını değiştirmek `version`'ı artırır.

```json
{ "transactionId": "…", "occurredAt": "2026-03-15T10:00:00+00:00", "mti": "0200", "transactionType": "Sale",
  "responseCode": "00", "irc": "0000", "maskedCardNumber": "411111********11", "accountNo": "TR01",
  "email": "musteri@example.com", "amount": 10.00, "currencyCode": "840", "localAmount": 492.15,
  "cardAcceptor": "PAYMENT SYSTEM POS SIMULATOR", "transactionDate": "20260315", "localTime": "125959",
  "terminalId": "TERM0001", "stan": "000001", "eventType": "TransactionCompleted", "version": 1 }
```

Ayarlar (`appsettings.json` → `Kafka`, ya da `Kafka__BootstrapServers`): `BootstrapServers` (`localhost:9094`; compose'da `kafka:9092`), `Topic`, `MessageTimeoutMs` (30000).

## Notification (PaymentSystem.Notification)

.NET 9 worker servisi (HTTP endpoint'i yok). `payment.transaction-events` topic'ini `notification` consumer group'uyla okur.

- **Kime:** sadece **onaylı satış** (`transactionType = Sale` ve F39 `00`) bildirilir; red, bakiye sorgu ve bilinmeyen olay tipleri atlanır. E-posta olaydaki `email`'e (`BankAccount.Email`) gider; hesabın e-postası yoksa loglanıp geçilir.
- **İçerik:** maskeli kart no, tutar ve para birimi, dövizse TL karşılığı, işyeri (F43), tarih / saat ve işlem no (Guid).
- **Tekrar:** Kafka bir olayı birden fazla teslim edebilir; gönderilen son 10.000 işlemin Guid'i bellekte tutulur, aynı işleme ikinci e-posta gitmez.
- **Hata:** gönderilemeyen e-posta `MaxAttempts` (3) kez, `RetryDelaySeconds` (5) arayla denenir, sonra loglanıp geçilir: tüketici o olayda takılı kalmaz. Offset olay işlendikten sonra saklanır; servis olayın ortasında kapanırsa açılınca aynı olay yeniden gelir.
- **Topic:** servis açılışta topic yoksa oluşturur (Payment henüz hiç olay yazmamış olabilir). Group ilk kez açılıyorsa topic'in başından okur.

### Gmail ile gerçek gönderim
1. Gönderen Google hesabında 2 adımlı doğrulamayı aç.
2. https://myaccount.google.com/apppasswords adresinden bir uygulama şifresi oluştur (16 hane).
3. `.env`'e yaz ve servisi yeniden başlat:
   ```
   SMTP_USER=gonderen@gmail.com
   SMTP_PASSWORD=abcdefghijklmnop
   ```
   `docker compose up -d --build notification`
4. Bir hesaba e-posta tanımla (`PUT /api/bank-accounts/{accountNo}` ile `"email": "..."`), o hesabın kartıyla Gate'ten satış yap; e-posta gelen kutusuna düşer. Akış `docker logs -f payment_system_notification`'da izlenir.

Gmail dışında bir sunucu için `SMTP_HOST`, `SMTP_PORT`, `SMTP_FROM` verilebilir. `SMTP_USER` / `SMTP_FROM`'dan biri yoksa servis açılışta hata verip durur.

### Ayarlar
`src/PaymentSystem.Notification/appsettings.json` → `Kafka` (`BootstrapServers`, `Topic`, `GroupId`) ve `Smtp` (`Host`, `Port`, `Security` = `StartTls` / `SslOnConnect` / `None`, `User`, `Password`, `From`, `FromName`, `MaxAttempts`, `RetryDelaySeconds`); ya da `Smtp__User` gibi ortam değişkenleri.

### Çalıştırma
```bash
# Docker (hepsi birlikte)
docker compose up -d --build
docker logs -f payment_system_notification

# lokal (Kafka container'ı ayaktayken; notification container'ı durdurulmuş olmalı)
$env:Smtp__User = "gonderen@gmail.com"
$env:Smtp__Password = "<uygulama şifresi>"
dotnet run --project src/PaymentSystem.Notification
```

## Testler ve CI

| Test projesi | Kapsam | Gereken |
|---|---|---|
| `tests/PaymentSystem.Gate.Tests` | HTTP endpoint'in alan doğrulaması, Gate'in karar mantığı (sahte Payment ile), gerçek RabbitMQ üzerinden request-reply (sahte Payment cevaplayıcısıyla: eşzamanlı istekler, geçersiz cevap, timeout + TTL, geç cevap), ISO8583 pack/unpack ve framing | **Docker** |
| `tests/PaymentSystem.Payment.Tests` | İşlem akışı (idempotency, eşzamanlılık, tüm red kodları, döviz çevrimi), kuyruk tüketicisi (gerçek RabbitMQ + SQL: cevap, `30`, süresi dolmuş istek, DB yokken `91`), 4 CRUD controller + Dapper repository'leri, model doğrulama, TCMB ayrıştırma + kur cache'i + arka plan servisi (sahte HTTP ile), işlem olayları (içerik, hangi kararda yayımlandığı, gerçek Kafka'ya yazım) | **Docker** |
| `tests/PaymentSystem.Notification.Tests` | Bildirim kuralları (sadece onaylı satış, e-postasız hesap, tekrar gelen olay, yeniden deneme), e-posta içeriği, gerçek Kafka'dan okuma (bozuk olay atlanır), SMTP gönderimi (Mailpit container'ına; gerçek e-posta gitmez) | **Docker** |

Testler [Testcontainers](https://dotnet.testcontainers.org/) ile gerçek SQL Server, RabbitMQ, Kafka ve Mailpit container'ları başlatır (compose'dakiyle aynı imajlar). SQL Server'da `docker/sql/init/*.sql` script'leri çalıştırılır; yani SQL'ler compose'daki DB ile birebir aynı şemaya karşı test edilir. Testten önce Docker Desktop açık olmalı (GitHub Actions'ın ubuntu runner'ında Docker hazır).

```bash
dotnet test -p:CollectCoverage=true
```
Line coverage her test projesi için ayrı ayrı %80'in altındaysa komut hata verir (ayarlar test `.csproj`'larında; `Program.cs` coverage dışı). Rapor: `tests/*/TestResults/coverage.cobertura.xml`.

GitHub Actions (`.github/workflows/ci.yml`), `master`'a açılan her pull request'te ve `master`'a yapılan her push'ta restore → build → test + coverage kapısını çalıştırır; coverage özeti job summary'de görünür. `master` üzerindeki ruleset, `build-and-test` check'i geçmeden merge'e izin vermez.

## Sıradaki Adımlar
- Cevap sözleşmesini genişletmek: F38 (onay kodu), F37 (RRN), F54 (bakiye sorgusu cevabı).
- Timeout'ta reversal (`0400`): Gate cevap alamadığı işlem için kuyruğa iptal mesajı bırakır, Payment açıldığında işler (store & forward).
- Gate'e kimlik doğrulama (API key / mTLS); Payment'ın yönetim endpoint'lerini iç ağa almak.
- Diğer asenkron işler (mutabakat, fraud) `payment.transaction-events`'i kendi consumer group'larıyla okuyabilir.
- Bildirim geçmişi: gönderilen / gönderilemeyen e-postaların kaydı (web arayüzünde göstermek için).
- DB şemasını ISO formatına uydurmak: `DebitTransaction.F13_TransactionDate` şu an `CHAR(8)` yyyymmdd (ISO: MMDD), `Card.ExpiryDate` MMYY (ISO F14: YYMM).
- Para transferinde e-posta bildirimi (Notification şu an sadece onaylı satışı bildirir); başka bankanın kartına transfer (off-us).
