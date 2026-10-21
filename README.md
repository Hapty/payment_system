# Payment System

Kartlı ödeme işlemlerini (satış, bakiye sorgu, karttan karta transfer) uçtan uca işleyen, eğitim amaçlı ama gerçek
bankacılık kavramlarıyla kurulmuş bir ödeme sistemi. .NET 9, SQL Server, RabbitMQ, Kafka ve SMTP kullanır.

Bu README projeyi **öğrenmek** için yazıldı: önce büyük resim ve kavramlar, sonra bir işlemin baştan sona yolculuğu,
ardından her endpoint'in tek tek anlatımı gelir. En sonda "hangi dosyayı hangi sırayla okumalıyım" diye bir öğrenme yolu var.

## İçindekiler

1. [Büyük resim](#1-büyük-resim)
2. [Temel kavramlar (sözlük)](#2-temel-kavramlar-sözlük)
3. [Kurulum ve çalıştırma](#3-kurulum-ve-çalıştırma)
4. [Uygulamalı lab: sıfırdan uçtan uca test](#4-uygulamalı-lab-sıfırdan-uçtan-uca-test)
5. [Uçtan uca akış: bir satışın yolculuğu](#5-uçtan-uca-akış-bir-satışın-yolculuğu)
6. [Endpoint'ler — Gate](#6-endpointler--gate-işlem-girişi)
7. [Endpoint'ler — Payment yönetim API'si](#7-endpointler--payment-yönetim-apisi)
8. [Servisler arası sözleşmeler (RabbitMQ, Kafka)](#8-servisler-arası-sözleşmeler)
9. [Karar motoru: kontroller, IRC ve F39](#9-karar-motoru-kontroller-irc-ve-f39)
10. [Veritabanı](#10-veritabanı)
11. [Önemli tasarım konuları](#11-önemli-tasarım-konuları)
12. [Notification servisi](#12-notification-servisi)
13. [Ayarlar ve ortam değişkenleri](#13-ayarlar-ve-ortam-değişkenleri)
14. [Testler, CI ve branch akışı](#14-testler-ci-ve-branch-akışı)
15. [Kod haritası](#15-kod-haritası)
16. [Öğrenme yolu](#16-öğrenme-yolu)
17. [Bilinen sınırlar ve sıradaki adımlar](#17-bilinen-sınırlar-ve-sıradaki-adımlar)

---

## 1. Büyük resim

```
                         (1) HTTP/JSON                (2) RabbitMQ request-reply
 İstemci (Postman/POS) ───────────────────► GATE ─────────────────────────────► PAYMENT ──(3) SQL──► SQL Server
                       POST :8080             │      kuyruk: payment.transactions │
                       /api/transactions      │                                   │
                       ◄──────────────────────┘ ◄─────── amq.rabbitmq.reply-to ───┘
                         (6) { mti, F39, F11 }           (5) { responseCode }      │
                                                                                   │ (4) Kafka (bloklamadan)
                                                         topic: payment.transaction-events
                                                                                   ▼
                                                                             NOTIFICATION ──SMTP──► müşterinin e-postası

 Yönetici (Postman) ──HTTP──► PAYMENT :5002  (hesap, kart, işyeri, tanım tabloları CRUD + kur cache'i)
                                 ▲
                     TCMB today.xml (saatte bir kur çekilir)
```

| Servis / parça | Klasör | Ne yapar | Port |
|---|---|---|---|
| **Gate** | `src/PaymentSystem.Gate` | İşlemlerin **tek giriş kapısı**. JSON'u ISO8583 kurallarına göre doğrular, isteği RabbitMQ ile Payment'a gönderir, cevabı bekleyip istemciye döner. İş kuralı bilmez. | `8080` |
| **Payment** | `src/PaymentSystem.Payment` | Sistemin **beyni**. Kuyruktan gelen işlemi karara bağlar (onay / red), parayı hareket ettirir, kaydı yazar, Kafka'ya olay basar. Ayrıca yönetim için CRUD API sunar. | `5002` |
| **Notification** | `src/PaymentSystem.Notification` | Kafka'daki olayları dinler; onaylı satış ve transferlerde müşteriye e-posta atar. HTTP endpoint'i yoktur. | — |
| **Iso8583** kütüphanesi | `src/PaymentSystem.Iso8583` | ISO8583 mesaj modeli, alan tanımları, pack/unpack, TCP framing, PAN maskeleme ve log biçimi. Gate ve Payment ortak kullanır. | — |
| SQL Server | `docker/sql` | Hesaplar, kartlar, işlemler, tanım tabloları. | `1433` |
| RabbitMQ | `docker-compose.yml` | Gate ↔ Payment arası senkron (request-reply) haberleşme. | `5672`, arayüz `15672` |
| Kafka | `docker-compose.yml` | Payment'ın "ne oldu" olayları; asenkron tüketiciler için. | `9094` |

**Neden üç ayrı servis?**

- **Gate** dış dünyaya açık kapıdır: format kontrolü, protokol çevirisi. İş kuralı burada olmaz; böylece ileride TCP/ISO8583 gibi
  başka bir giriş eklense de karar mantığı tek yerde (Payment) kalır.
- **Payment** ile Gate arasında kuyruk olması, Payment kapalıyken bile Gate'in ayakta kalmasını ve kontrollü `91` dönmesini sağlar.
- **Notification** işlemi bekletmemeli: e-posta sunucusu yavaş ya da kapalıysa satış yine de anında onaylanmalı. Bu yüzden
  Kafka üzerinden asenkron çalışır. Yarın fraud/raporlama gibi yeni tüketiciler Payment'a dokunmadan eklenebilir.

---

## 2. Temel kavramlar (sözlük)

Kodda ve bu dokümanda sürekli geçen terimler:

| Terim | Anlamı |
|---|---|
| **ISO 8583** | Kartlı ödeme mesajlarının uluslararası standardı. Mesaj = MTI + bitmap + numaralı alanlar (F2, F3, …). Bu projede 1987 sürümü kullanılır. |
| **MTI** | Message Type Indicator, 4 hane. `0200` = finansal istek, `0210` = onun cevabı. 3. hane çiftse istek, tekse cevaptır (`0800`→`0810`). |
| **Bitmap** | Mesajda hangi alanların bulunduğunu gösteren bit dizisi. n. bit 1 ise Fn mesajda vardır. (`IsoMessagePacker`) |
| **F2 – PAN** | Kart numarası (Primary Account Number). Loglarda daima maskelidir: `411111********11`. |
| **F3 – Processing code** | İşlemin türü: `000000` satış, `310000` bakiye sorgu, `400000` karttan karta transfer. |
| **F4 – Tutar** | Kuruş cinsinden: `15000` = 150,00. |
| **F11 – STAN** | System Trace Audit Number: terminalin işleme verdiği sıra no. İstemci cevabı isteğiyle bununla eşleştirir. |
| **F14** | Kartın son kullanma tarihi, `YYMM`. (DB'deki `Card.ExpiryDate` ise `MMYY`.) |
| **F18 – MCC** | Merchant Category Code: işyeri tipi (`5411` market, `5399` genel mağaza, `4829` para transferi …). İstekteki F18, işyerinin tablodaki MCC'siyle aynı olmalı. |
| **F22 – Entry mode** | Kartın nasıl okunduğu: `05x` çip, `07x` temassız, `01x` elle giriş, `81x` e-ticaret. |
| **F39 – Response code** | Kararın kendisi: `00` onay, `51` yetersiz bakiye, `14` geçersiz kart, `91` sistem ulaşılamıyor … |
| **F41 / F42 / F43** | Terminal no / işyeri no / işyeri adı-yeri. F42 ve F43 sabit uzunluktadır (15 / 40), kısa değer sağdan boşlukla doldurulur. Transferde F42 bir mağaza değil, işlemin başladığı **kanaldır** (mobil uygulama, ATM). |
| **F48** | "Ek veri" alanı; bu sistemde **transferde alıcı kart no**. |
| **F49** | Para birimi, ISO 4217 sayısal: `949` TRY, `840` USD, `978` EUR. |
| **CVV2** | Kartın arkasındaki 3-4 hane. ISO8583:1987'de alanı yoktur, JSON'da ayrı `cvv` alanıyla gelir. Kart fiziksel olarak yokken (elle giriş, e-ticaret) zorunludur. |
| **IRC** | Internal Response Code. Payment'ın kararı önce 4 haneli bir iç koddur (`3002` = bakiye yetersiz); POS'a dönen F39 bir tablodan (`InternalResponseCode`) bulunur. Böylece birkaç sebep aynı F39'a çıkabilir ve eşleme kod değiştirmeden değişebilir. IRC DB kaydında tutulmaz; **Payment logunda** ve Kafka olayında görünür. |
| **Otc / Ots** | İşlem tipini tanımlayan iki haneli kod çifti. `MTI + F3 → (Otc, Ots) → işlem adı` (`Sale`, `MoneyTransfer`, `BalanceInquiry`). |
| **On-us** | Gönderen ve alıcı kartın ikisi de bu bankada. Bu sistemdeki transfer sadece on-us'tır. |
| **Request-reply** | Kuyruk üzerinden "istek gönder, cevabını bekle" kalıbı. `CorrelationId` ile istek-cevap eşleşir. |
| **Idempotency** | Aynı istek iki kez gelirse sonucun bir kez olması (para iki kez düşmemesi). |
| **TTL** | Time To Live: kuyruktaki mesajın ömrü. Süresi dolan istek silinir. |
| **Consumer group** | Kafka'da bir topic'i okuyan tüketici grubu. Her grup tüm olayları bağımsız okur. |
| **PCI DSS** | Kart verisi güvenlik standardı. Gereği: tam PAN loglanmaz, CVV hiç saklanmaz. |

---

## 3. Kurulum ve çalıştırma

### Gereksinimler
- Docker Desktop
- .NET 9 SDK (sadece lokal çalıştırma ve testler için)
- Postman ya da VS Code / Rider'ın `.http` dosya desteği

### İlk kurulum
```bash
cp .env.example .env
```
`.env` içini doldur:

| Anahtar | Açıklama |
|---|---|
| `MSSQL_SA_PASSWORD` | SQL Server `sa` şifresi (güçlü olmalı, yoksa SQL Server açılmaz) |
| `RABBITMQ_PASSWORD` | RabbitMQ `payment` kullanıcısının şifresi |
| `CARD_SECRET_KEY` | CVV/PIN hash anahtarı, **64 hex karakter**. PowerShell: `-join ((1..32) \| % { '{0:X2}' -f (Get-Random -Max 256) })` |
| `SMTP_USER`, `SMTP_PASSWORD` | Gmail adresi ve 16 haneli uygulama şifresi (bkz. [Notification](#12-notification-servisi)). **Boşsa Notification servisi açılmaz**, diğerleri çalışır. |

### Hepsini ayağa kaldır
```bash
docker compose up -d --build
docker compose ps
```

Açılış sırası compose'daki `depends_on` ile garanti edilir:

```
db (healthy) ──► db-init (SQL script'leri çalışır, biter) ──┐
rabbitmq (healthy) ─────────────────────────────────────────┼──► payment
kafka (healthy) ────────────────────────────────────────────┘      │
rabbitmq (healthy) ──► gate                                   kafka ──► notification
```

`db-init`, `docker/sql/init/` altındaki script'leri sırayla çalıştırıp `PaymentSystem` veritabanını, tabloları ve seed
verisini oluşturur. Script'ler idempotent'tir (`IF NOT EXISTS` / `MERGE`), her açılışta tekrar çalışması zararsızdır.

| Adres | Ne |
|---|---|
| http://localhost:8080/api/transactions | Gate — işlem girişi |
| http://localhost:5002/api/... | Payment — yönetim API'si |
| http://localhost:8080/swagger | Gate Swagger UI |
| http://localhost:5002/swagger | Payment Swagger UI |
| http://localhost:15672 | RabbitMQ arayüzü (`payment` / `RABBITMQ_PASSWORD`) |
| `localhost:1433` | SQL Server (DBeaver vb. ile bağlanırken Database: `PaymentSystem`) |

### Loglar
```bash
docker logs -f payment_system_gate
docker logs -f payment_system_payment
docker logs -f payment_system_notification
```
Loglar tek satırlıdır ve her satır metot adıyla başlar (`HandleAsync: ...`). ISO alanları her iki serviste de aynı biçimde
yazılır: `F2 (Kart No)=411111********11 F11 (STAN)=000001`. Bir işlemi Gate ve Payment loglarında STAN ile takip edebilirsin.

### Veriyi sıfırlamak
```bash
docker compose down      # container'lar gider, DB verisi kalır (volume: payment_system_mssql_data)
docker compose down -v   # DB verisi de silinir
```
Kafka'nın volume'ü yoktur; olaylar `down` ile silinir.

### Bir servisi lokalde (IDE'den) çalıştırmak
Aynı portu kullandıkları için önce ilgili container'ı durdur (`docker stop payment_system_payment`).

```powershell
# Payment (DB, RabbitMQ, Kafka container'ları ayakta olmalı)
$env:MSSQL_SA_PASSWORD = "<şifre>"; $env:RABBITMQ_PASSWORD = "<şifre>"; $env:CARD_SECRET_KEY = "<64 hex>"
dotnet run --project src/PaymentSystem.Payment --launch-profile http      # http://localhost:5002

# Gate
$env:RABBITMQ_PASSWORD = "<şifre>"
dotnet run --project src/PaymentSystem.Gate --launch-profile http         # http://localhost:8080

# Notification
$env:Smtp__User = "gonderen@gmail.com"; $env:Smtp__Password = "<uygulama şifresi>"
dotnet run --project src/PaymentSystem.Notification
```
Lokalde Kafka adresi `localhost:9094`, container içinde `kafka:9092`'dir (compose iki listener açar).

### Swagger (API dokümanı ve deneme arayüzü)
Swagger (OpenAPI), API'nin makinece okunabilen tarifidir: hangi endpoint var, hangi gövdeyi alır, hangi durum kodlarıyla
ne döner. Tarif koddan üretilir, kod değişince kendiliğinden güncellenir.

| Adres | Ne |
|---|---|
| http://localhost:8080/swagger · http://localhost:5002/swagger | Swagger UI: endpoint'leri gör, "Try it out" ile tarayıcıdan istek at |
| http://localhost:8080/openapi/v1.json · http://localhost:5002/openapi/v1.json | Ham OpenAPI şeması: Postman'a import edilebilir |

- Şemayı .NET 9'un yerleşik `Microsoft.AspNetCore.OpenApi` paketi üretir (`AddOpenApi` / `MapOpenApi`); arayüz
  `Swashbuckle.AspNetCore.SwaggerUI` paketinden gelir ve sadece bu şemayı gösterir.
- İkisi de `Swagger:Enabled` ayarıyla açılır (varsayılan `true`), ortamdan bağımsızdır: hem Docker'da hem lokalde çalışır.
  Kapatmak için: `Swagger__Enabled=false`.
- Açıklamalar koddan gelir: Gate'te `.WithSummary` / `.WithDescription` ve istek alanlarındaki `[Description]`
  (F2, F3 ... ne demek); Payment'ta action'lardaki `[EndpointSummary]` ve `[ProducesResponseType]` (hangi durum kodu döner).
  Gate'in `POST /api/transactions` gövdesi örnek bir satış isteğiyle dolu gelir (`Program.cs`'teki operation transformer).
- ⚠️ "Try it out" ile atılan istek **gerçektir**: Gate'teki örnek satış onaylanırsa bakiye düşer, kayıt açılır, e-posta gider.
  Aynı örneği ikinci kez gönderirsen tekrar sayılır ([11.1](#111-idempotency-aynı-istek-iki-kez-gelirse)); yeni işlem için F11/F12'yi değiştir.

---

## 4. Uygulamalı lab: sıfırdan uçtan uca test

Bu bölümde boş bir veritabanından başlayıp sistemi baştan sona test ediyorsun: önce veriyi oluşturuyorsun, sonra bir satış
ve bir karttan karta transfer (KKPT) yapıyorsun, ardından sistemi bilerek reddettiriyorsun. Her adımda **ne beklediğini**
ve **nasıl doğrulayacağını** göreceksin. İstekleri Postman'den, Swagger'dan ya da hazır `.http` dosyalarından
(`src/PaymentSystem.Payment/PaymentSystem.Payment.http`, `src/PaymentSystem.Gate/PaymentSystem.Gate.http`) atabilirsin.

> **Bir sonucu doğrulamanın üç yolu var**, lab boyunca üçünü de kullan:
>
> | Nereye bakıyorsun | Ne görürsün | Nasıl |
> |---|---|---|
> | Payment API | Bakiye, kartın son işlemi | `GET /api/bank-accounts/{no}`, `GET /api/cards` |
> | Veritabanı | İşlem kaydının kendisi (`DebitTransaction`) | `sqlcmd` (bkz. [4.7](#47-veritabanından-son-kontrol)) |
> | Loglar | Kararın **asıl sebebi** (IRC) | `docker logs payment_system_payment` |

### 4.0 Hazırlık
```bash
docker compose up -d --build
docker compose ps          # db, payment, gate, rabbitmq, kafka, notification "Up"; db-init çalışıp biter
```
Tanım tabloları (işlem tipleri, giriş yöntemleri, cevap kodları, işyerleri) `db-init` ile **otomatik dolar**; hesap ve kart
gelmez. Önce seed'in geldiğini gör:
```http
GET http://localhost:5002/api/merchants               # TRENDYOL (5399), GETIR, IYZICO, NETFLIX
GET http://localhost:5002/api/mti-processing-codes    # 000000 satış, 400000 transfer, 310000 bakiye sorgu
```

### 4.1 Veriyi oluştur
> **Kural: boş kolonlu kayıt açılmaz.** Hesapta ve kartta her alan zorunludur. Tek istisnalar: kartın `lastTransaction*`
> alanları (yeni kartta işlem yok) ve temassıza kapalı kartın `contactlessLimit`'i. Eksik alan → `400`. Ayrıntı: [10.1](#101-boş-kalabilen-kolonlar).

**Gönderen hesap ve kartı**
```http
POST http://localhost:5002/api/bank-accounts
Content-Type: application/json

{ "accountNo": 1000000001, "accountStatus": "Active", "balance": 1000.00, "email": "sen@example.com" }
```
```http
POST http://localhost:5002/api/cards
Content-Type: application/json

{ "cardNumber": "4111111111111111", "accountNo": 1000000001, "cardType": "Debit",
  "expiryDate": "1228", "contactlessAllowed": true, "contactlessLimit": 750.00 }
```
Cevapta kartın `cvv` ve `pin`'i **bir kez** döner — not al, bir daha hiçbir yerde göremezsin (DB'de sadece hash'leri var):
```json
{ "id": 1, "cardNumber": "4111111111111111", "accountNo": 1000000001, "cardType": "Debit", "expiryDate": "1228",
  "lastTransactionDate": null, "lastTransactionAmount": null, "contactlessAllowed": true, "contactlessLimit": 750.00,
  "cvv": "482", "pin": "0731" }
```

**Alıcı hesap ve kartı** (transfer için; alıcı **başka bir hesapta** olmalı)
```http
POST http://localhost:5002/api/bank-accounts
Content-Type: application/json

{ "accountNo": 1000000002, "accountStatus": "Active", "balance": 500.00, "email": "alici@example.com" }
```
```http
POST http://localhost:5002/api/cards
Content-Type: application/json

{ "cardNumber": "5406680000000002", "accountNo": 1000000002, "cardType": "Debit",
  "expiryDate": "1228", "contactlessAllowed": true, "contactlessLimit": 750.00 }
```

**Transfer kanalı.** F42 her işlemde zorunludur, transferde de. Ama transferde F42 bir mağazayı değil, işlemin **başladığı
kanalı** (mobil uygulama, ATM, fintech) anlatır ve MCC'si genelde `4829` (Money Transfer) olur. "Trendyol'da para transferi"
anlamsız olacağı için bir kanal ekle:
```http
POST http://localhost:5002/api/merchants
Content-Type: application/json

{ "merchantId": "MOBILEBANK", "name": "MOBILE BANKING", "merchantType": "4829", "status": "Active" }
```

### 4.2 Satış
```http
POST http://localhost:8080/api/transactions
Content-Type: application/json

{
  "mti": "0200",
  "F2": "4111111111111111",
  "F3": "000000",
  "F4": 15000,
  "F49": "949",
  "F7": "1021100000",
  "F11": "000001",
  "F12": "100000",
  "F13": "20261021",
  "F14": "2812",
  "F18": "5399",
  "F22": "051",
  "F41": "TERM0001",
  "F42": "TRENDYOL       ",
  "F43": "TRENDYOL                 ISTANBUL     TR"
}
```
Alanları okumayı öğren:

| Alan | Değer | Neden böyle |
|---|---|---|
| `F4` | `15000` | Tutar **kuruş** cinsinden: 150,00 TL |
| `F14` | `2812` | Kartın son kullanması `YYMM`. Karttaki `expiryDate` ise `MMYY` (`1228`) — ters yazılır! |
| `F18` | `5399` | İşyerinin MCC'siyle **aynı** olmalı (TRENDYOL = 5399) |
| `F22` | `051` | Çip. Kart fiziksel olarak okunduğu için CVV istenmez |
| `F42` | `"TRENDYOL       "` | Sabit **15** karakter: kısa no sağdan boşlukla doldurulur |
| `F43` | 40 karakter | Sabit **40**: 25 isim + 13 şehir + 2 ülke |
| `F11` / `F12` | `000001` / `100000` | Her yeni işlemde değiştir (bkz. [4.5](#45-aynı-isteği-tekrar-gönder-idempotency)) |

**Beklenen:** `{ "mti": "0210", "F39": "00", "F11": "000001" }`

**Doğrula:** `GET /api/bank-accounts/1000000001` → `balance: 850.00`. `GET /api/cards/1` → `lastTransactionAmount: 150.00`.
E-posta ayarlıysa "Kartınızla 150,00 TRY tutarında satış yapıldı" maili gelir.

### 4.3 Karttan karta transfer (KKPT)
Satıştan farkı: `F3 400000`, `F48` = alıcı kart no, kanal `MOBILEBANK` / `4829`.
```http
POST http://localhost:8080/api/transactions
Content-Type: application/json

{
  "mti": "0200",
  "F2": "4111111111111111",
  "F3": "400000",
  "F4": 20000,
  "F49": "949",
  "F7": "1021101000",
  "F11": "000002",
  "F12": "101000",
  "F13": "20261021",
  "F14": "2812",
  "F18": "4829",
  "F22": "051",
  "F41": "TERM0001",
  "F42": "MOBILEBANK     ",
  "F43": "MOBILE BANKING           ISTANBUL     TR",
  "F48": "5406680000000002"
}
```
**Beklenen:** `F39 00`. **Doğrula:** gönderen `650.00`, alıcı `700.00`. İki tarafa da e-posta gider. Düşme ve ekleme
aynı SQL transaction'ındadır: ya ikisi birden olur ya hiçbiri.

### 4.4 Red senaryoları: sistemi bilerek reddettir
4.3'teki transfer gövdesini al; her senaryoda **sadece tablodaki alanı** değiştir, `F11` ve `F12`'yi de yeni bir değer yap
(örn. `000003` / `110000`, `000004` / `111000` …).

| Senaryo | Değiştir | Beklenen F39 | IRC (logda) | Ne öğretiyor |
|---|---|---|---|---|
| Yetersiz bakiye | `F4: 100000` (1.000 TL) | `51` | `3002` | Bakiye kontrolü ve düşme tek SQL'de (`WHERE Balance >= @amount`) |
| Aynı hesaba transfer | `F48: "4111111111111111"` | `12` | `1006` | Alıcı kart **başka hesapta** olmalı |
| Yanlış MCC | `F18: "5399"` | `03` | `4003` | F18 işyerinin MCC'siyle aynı olmalı |
| Bilinmeyen işyeri | `F42: "BILINMEYEN     "` | `03` | `4001` | F42 `Merchant` tablosunda olmalı |
| Olmayan kart | `F2: "4000000000000001"` | `14` | `2001` | |
| Olmayan alıcı | `F48: "5406680000000099"` | `14` | `2007` | |
| E-ticaret, CVV yok | `F3: "000000"`, `F22: "812"`, `F18: "5399"`, `F42`/`F43`: TRENDYOL, `F48`'i sil | `N7` | `2006` | Kart fiziksel yokken CVV2 zorunlu; `"cvv": "<4.1'de not aldığın>"` ekleyince `00` |

Her redde HTTP yine **`200`**'dür; karar `F39`'dadır. Bakiyeler değişmez. Reddedilen işlem de `DebitTransaction`'a
kendi F39'uyla yazılır (iz kalsın diye) ama kartın `lastTransaction*`'ı güncellenmez ve e-posta gitmez.

**Aynı F39, farklı sebep: IRC'yi nereden göreceksin?** `InternalResponseCode` tablosunda F39'u `12` olan beş satır var
(`1001`, `1002`, `1003`, `1004`, `1006`). F39 POS'a giden **kaba** koddur; asıl sebep IRC'dir ve DB kaydında tutulmaz.
Logda her redde yazılır:
```bash
docker logs payment_system_payment --since 10m | findstr IRC        # Linux/macOS: grep IRC
```
```
TransferAsync: declined F11 (STAN)=000004 ... F48 (Alıcı Kart No)=411111********11 is on the same account 1000000001 -> IRC 1006
DecideAsync: ... not approved: F39 (Cevap Kodu)=12 Transfer to the same account (IRC 1006) transaction d5f71c98-... account 1000000001
```
Logdaki `transaction` Guid'i `DebitTransaction.Guid` ile aynıdır: log satırını DB kaydıyla böyle eşleştirirsin. IRC ayrıca
Kafka olayında (`irc` alanı) taşınır. Gerçek bankalarda da böyledir: dışarıya kaba kod, ayrıntı bankanın içinde kalır.

### 4.5 Aynı isteği tekrar gönder (idempotency)
4.3'teki onaylı transferi **hiçbir alanını değiştirmeden** tekrar gönder.

**Beklenen:** yine `F39 00` (ilk kararın kendisi), ama bakiyeler **değişmez**, `DebitTransaction`'a yeni satır **açılmaz**.
Logda: `PreviousResponseAsync: duplicate request ... - returning previous F39 (Cevap Kodu)=00`.

Neden? Kaydın anahtarı isteğin `MTI | F2 | F3 | F4 | F13 | F12` alanlarından üretilir; aynı istek aynı anahtarı üretir ve
ikinci `INSERT` PK'ya takılır (bkz. [11.1](#111-idempotency-aynı-istek-iki-kez-gelirse)). Yeni bir işlem yapmak
istediğinde bu yüzden en az `F12`'yi değiştirmen gerekir.

### 4.6 Lab sonunda beklenen durum
| | Başlangıç | Satış | KKPT | Redler + tekrar | **Son** |
|---|---|---|---|---|---|
| Gönderen (1000000001) | 1000,00 | −150 | −200 | 0 | **650,00** |
| Alıcı (1000000002) | 500,00 | | +200 | 0 | **700,00** |

### 4.7 Veritabanından son kontrol
```bash
docker exec -it payment_system_db /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P '<MSSQL_SA_PASSWORD>' -C -d PaymentSystem \
  -Q "SELECT CreatedAt, F3_ProcessingCode, F4_Amount, F18_MerchantCode, F39_ResponseCode, F42_MerchantId, F48_RecipientCardNo FROM dbo.DebitTransaction ORDER BY CreatedAt"
```
Her işlem (onay ve red) bir satırdır, tekrar gelen istek satır açmaz. Satışta `F48_RecipientCardNo` NULL'dır; bu kolon
sadece transferde dolar. Başka bir kolonda NULL görüyorsan bir şey ters gitmiştir.

### 4.8 Bir şey ters giderse
| Ne görüyorsun | Ne demek | Ne yap |
|---|---|---|
| `400` ve **gövde boş** (`content-length: 0`), Gate logunda hiçbir satır yok | JSON **okunamadı**: istek bizim koda hiç ulaşmadı. Eksik virgül/tırnak, yapıştırırken kalan eski metin, ya da sayı olması gereken alan tırnakta (`"F4": "5000"`) | Gövdeyi tamamen sil (Ctrl+A), yeniden yapıştır |
| `400` ve `errors: { "F18": ["F18 is required."] }` | JSON geçerli ama bir alan eksik ya da formatı bozuk; **bizim** doğrulamamız. Hangi alanın ne sorunu olduğu yazar | Belirtilen alanı düzelt |
| `400`, `errors: { "$": ["... missing required properties including: 'balance'"] }` | Hesap/kartta değer tipli zorunlu alan (`balance`, `contactlessAllowed`) body'de yok. Bu hata önce gelir; diğer eksikler o düzelince görünür | Alanı ekle |
| `200` ve `F39` `00` değil | İstek geçerliydi, **sistem reddetti** | IRC'ye logdan bak ([4.4](#44-red-senaryoları-sistemi-bilerek-reddettir)), [Bölüm 9](#9-karar-motoru-kontroller-irc-ve-f39)'daki tabloda bul |
| `F39 00` ama bakiye değişmedi | Tekrar gelen istek: ilk kararın cevabı döndü | `F12`'yi (ve `F11`'i) değiştir |
| Swagger'da "Try it out" → Execute bakiyeyi düşürdü | Swagger örnek gövdesi **gerçek** bir satıştır, sisteme normal işlem olarak gider | Denemeden önce F11/F12/F13'ü değiştir; istemediğin işlemi Postman'deki gibi düşün |
| `F39 91` | Payment 10 sn içinde cevap vermedi ya da döviz kuru yok/bayat | `docker compose ps`, Payment logu; dövizde `GET /api/exchange-rates` |
| E-postaların bir kısmı **Gereksiz**'e, bir kısmı **Diğer**'e düşüyor | Kişisel Gmail'den "kartınızdan para transferi" içerikli otomatik mail, Microsoft'un filtresinde eşiğe yakın puan alıyor; "Diğer" ise sadece sıralama | Bir maili "Gereksiz değil" işaretle, göndereni Güvenilir Gönderenler'e ekle. Gerçek sistemde: kendi alan adı + SPF/DKIM/DMARC + transactional mail servisi |
| `docker ps`: `failed to connect to the docker API` | Docker Desktop kapalı | Docker Desktop'ı aç |

---

## 5. Uçtan uca akış: bir satışın yolculuğu

[4.2](#42-satış)'deki satış isteğinin sistemde izlediği yol, dosya ve metot adlarıyla:

```
İstemci          Gate                              RabbitMQ            Payment                                 SQL        Kafka     Notification
   │ POST /api/transactions                            │                   │                                      │          │            │
   │──────────────►│ ① TransactionEndpoint.HandleAsync │                   │                                      │          │            │
   │               │   JSON → IsoMessage, alan doğrula │                   │                                      │          │            │
   │               │   (hata → 400)                    │                   │                                      │          │            │
   │               │ ② TransactionHandler.ProcessAsync │                   │                                      │          │            │
   │               │   0200 mü? zorunlu alanlar?       │                   │                                      │          │            │
   │               │ ③ RabbitMqPaymentClient           │                   │                                      │          │            │
   │               │   publish (CorrelationId, TTL) ──►│ payment.transactions                                     │          │            │
   │               │   cevabı bekle (≤10 sn)           │──────────────────►│ ④ TransactionQueueConsumer           │          │            │
   │               │                                   │                   │   süresi dolmuş mu? JSON geçerli mi?  │          │            │
   │               │                                   │                   │ ⑤ TransactionHandler.AuthorizeAsync  │          │            │
   │               │                                   │                   │   işlem tipi (cache) → UUID v5        │          │            │
   │               │                                   │                   │   BEGIN TRAN; INSERT DebitTransaction ───────────►│          │            │
   │               │                                   │                   │   kontroller (işyeri, kart, CVV ...)  │          │            │
   │               │                                   │                   │   UPDATE Balance ... WHERE >= amount ─────────────►│          │            │
   │               │                                   │                   │   F39 yaz; COMMIT ─────────────────────────────────►│          │            │
   │               │                                   │                   │ ⑥ olay yayımla (beklemeden) ─────────────────────────────────►│            │
   │               │                                   │ reply-to          │ ⑦ { "responseCode": "00" } + ack     │          │            │
   │               │◄──────────────────────────────────│◄──────────────────│                                      │          │ ⑧ consume  │
   │◄──────────────│ ⑨ { mti: 0210, F39: 00, F11 }     │                   │                                      │          │──────────►│ e-posta
```

### ① Gate: HTTP isteğini al ve doğrula — `Gate/TransactionEndpoint.cs`
- JSON gövdesi `TransactionRequest` record'una bağlanır. JSON alan adları ISO numaralarıdır (`"F2"`), C# tarafında okunur isimler (`CardNumber`).
- `mti` 4 haneli bir **istek** MTI'ı değilse hemen `400`.
- Her alan `Iso87Fields`'taki tanımla (`Fixed`/`LLVar`/`LLLVar`, uzunluk, karakter seti) doğrulanır. `F4` sayı olarak gelir, 12 haneye sıfırla doldurulur.
- `0200` için zorunlu alanlar (`F2 F3 F4 F7 F11 F12 F13 F14 F18 F22 F41 F42 F43 F49`) eksikse hata listesine eklenir. DB'de kolonu olan her alan zorunludur, boş kolonlu kayıt açılmaz; opsiyonel olanlar sadece `F37` (saklanmaz) ve transferin `F48`'i.
- **F13 özel**: JSON'da `yyyymmdd` (gerçek bir tarih olmalı) gelir; ISO mesajına standart `MMDD` olarak girer, yıllı hali ayrıca Payment'a taşınır.
- `cvv` 3 ya da 4 rakam olmalı; değeri hiçbir yere loglanmaz.
- Herhangi bir hata varsa: `400 ValidationProblem` — her alanın hatası ayrı yazılır. İstek Payment'a **gitmez**.

### ② Gate: karar ver ya da ilet — `Gate/TransactionHandler.cs`
- MTI `0200` değilse (örn. `0800`) Payment'a gidilmez, `F39=12` döner.
- `0200` ise zorunlu alanlar tekrar kontrol edilir (savunma amaçlı; eksikse `30`), sonra `PaymentRequest`'e çevrilip iletilir.
- Gate **işlem tipini yorumlamaz** — satış mı transfer mi, Payment karar verir.
- Cevapta istemcinin eşleştirme için kullandığı alanlar (`F2 F3 F4 F7 F11 F12 F13 F37 F41 F42 F49`) geri yansıtılır (echo); HTTP cevabına bunlardan `F11` konur.

### ③ Gate → RabbitMQ — `Gate/Payment/RabbitMqPaymentClient.cs`
- Rastgele bir `CorrelationId` üretilir ve bir `TaskCompletionSource` ile bekleme listesine (`_pending`) eklenir.
- Mesaj `payment.transactions` kuyruğuna yazılır. Özellikleri:
  - `ReplyTo = amq.rabbitmq.reply-to` (RabbitMQ'nun direct reply-to'su; ayrı cevap kuyruğu açılmaz)
  - `Expiration` = `PaymentTimeoutSeconds` (TTL) ve `Timestamp` = gönderim anı
- `PaymentTimeoutSeconds` (10 sn) içinde cevap gelmezse, RabbitMQ'ya ulaşılamazsa ya da cevap bozuksa → `91`. Bu sınıf asla exception fırlatmaz.
- Bağlantı ilk istekte açılır; koparsa bir sonraki istekte yeniden kurulur. Yani RabbitMQ kapalıyken Gate açılır, istekler `91` alır.

### ④ Payment: kuyruktan al — `Payment/Transactions/TransactionQueueConsumer.cs`
- Arka plan servisi (`BackgroundService`); aynı anda en fazla **8** isteği işler (prefetch 8).
- `Timestamp + Expiration` geçmişse istek **işlenmez ve cevaplanmaz**: Gate zaten `91` döndü, şimdi para düşmek yanlış olur.
- JSON okunamıyorsa ya da `TransactionRequest` üzerindeki DataAnnotations geçmiyorsa → `30`.
- `TransactionHandler`'dan ya F39 döner ya `TransactionException` gelir; ikisinde de sadece F39 Gate'e yazılır, mesaj `ack`lenir (asla kuyruğa geri konmaz).

### ⑤ Payment: karar — `Payment/Transactions/TransactionHandler.cs`
1. `MTI + F3 → (Otc, Ots) → işlem adı` bulunur (`ParameterCache`, DB'ye gitmeden). Tanımsızsa `1001 → 12`, **kayıt açılmaz**.
2. TL karşılığı hesaplanır (TL ise kendisi, dövizse cache'teki TCMB kuruyla).
3. İsteğin alanlarından deterministik bir **UUID v5** üretilir; bu, kaydın PK'sıdır.
4. Tek SQL transaction'ı açılır ve `DebitTransaction` INSERT edilir. PK çakışırsa → bu bir **tekrar** isteğidir, ilk kararın F39'u döner.
5. Kontroller sırayla yapılır (tam liste: [Bölüm 9](#9-karar-motoru-kontroller-irc-ve-f39)). İlk başarısız kontrol bir IRC döner.
6. IRC → F39 tablodan bulunur, kayda yazılır, **COMMIT**. Reddedilen işlemler de kendi F39'uyla kaydedilir.
7. Onaysa F39 döner; redse commit'ten **sonra** `TransactionException` fırlatılır (kayıt geri alınmasın diye).

### ⑥ Payment → Kafka — `Payment/Events/KafkaTransactionEventPublisher.cs`
Commit edilen her karar (onay ve red) `TransactionCompleted` olayı olarak `payment.transaction-events`'e yazılır. `Produce`
bloklamaz; Kafka kapalı olsa bile Gate'e dönen cevap gecikmez.

### ⑦ – ⑨ Cevap geri döner
Payment `{ "responseCode": "00" }`'ı aynı `CorrelationId` ile reply-to'ya yazar → Gate bekleyen isteği bulur →
HTTP `200 { "mti": "0210", "F39": "00", "F11": "000001" }`.

### ⑧ Notification — `Notification/Events/TransactionEventConsumer.cs` → `TransactionNotifier.cs`
Olay onaylı bir `Sale` ya da `MoneyTransfer` ise ilgili hesap(lar)ın e-postasına SMTP ile bildirim gönderilir. Bu adım tamamen
asenkrondur; istemci cevabını çoktan almıştır.

---

## 6. Endpoint'ler — Gate (işlem girişi)

Base URL: `http://localhost:8080`

### `POST /api/transactions`

Sisteme işlem sokmanın **tek** yolu. Satış, bakiye sorgu ve transfer aynı endpoint'ten, `F3` ile ayrışarak gelir.

#### İstek alanları

| JSON | ISO alanı | Format | `0200`'de zorunlu? | Not |
|---|---|---|---|---|
| `mti` | — | 4 rakam, istek MTI'ı | evet | Sadece `0200` işlenir; başka istek MTI'ı `F39=12` alır |
| `F2` | Kart no (PAN) | 1–19 rakam (LLVAR) | evet | |
| `F3` | İşlem kodu | n6 | evet | `000000` satış · `310000` bakiye sorgu · `400000` transfer |
| `F4` | Tutar | JSON **sayı**, kuruş, en çok 12 hane | evet | `15000` = 150,00. Bakiye sorguda `0` |
| `F7` | Gönderim zamanı | n10 `MMDDhhmmss` | evet | |
| `F11` | STAN | n6 | evet | Cevapta geri döner |
| `F12` | Yerel saat | n6 `hhmmss` | evet | Idempotency anahtarına girer |
| `F13` | Yerel tarih | **n8 `yyyymmdd`**, gerçek tarih | evet | ISO'da MMDD'dir; Gate yıllı halini Payment'a ayrıca taşır |
| `F41` | Terminal no | tam 8 karakter (ans) | evet | |
| `F42` | İşyeri no | tam **15** karakter (ans) | evet | Kısa no sağdan boşlukla doldurulur: `"TRENDYOL       "`. `Merchant` tablosunda olmalı |
| `F49` | Para birimi | n3 | evet | `949` TRY, `840` USD, `978` EUR … |
| `F14` | Son kullanma | n4 `YYMM` | evet | Kartla uyuşmalı |
| `F18` | İşyeri tipi (MCC) | n4 | evet | İşyerinin MCC'siyle aynı olmalı |
| `F22` | Giriş yöntemi | n3 | evet | `051` çip · `071` temassız · `011` elle · `812` e-ticaret |
| `F37` | RRN | an12 | | Doğrulanır ve iletilir; şu an saklanmaz |
| `F43` | İşyeri adı/yeri | tam **40** karakter | evet | E-postada görünür |
| `F48` | Ek veri | ans, 1–999 (LLLVAR) | transferde | Bu sistemde **alıcı kart no** |
| `cvv` | — | 3 ya da 4 rakam | elle girişte / e-ticarette | ISO alanı değildir; loglanmaz, saklanmaz |

#### Cevaplar

| HTTP | Ne zaman | Gövde |
|---|---|---|
| `200` | İstek geçerliydi ve karara bağlandı — **onay da red de 200'dür** | `{ "mti": "0210", "F39": "<kod>", "F11": "<STAN>" }` |
| `400` | MTI ya da alan formatı bozuk / zorunlu alan eksik. Payment'a gidilmez | ASP.NET `ValidationProblem` |

Kararın kendisi her zaman `F39`'dadır. Gate'in kendi ürettiği kodlar:

| F39 | Anlamı |
|---|---|
| `12` | Desteklenmeyen MTI (`0200` dışı istek) |
| `30` | Zorunlu alan eksik (savunma; normalde 400'e takılır) |
| `91` | Payment 10 sn içinde cevap vermedi, RabbitMQ'ya ulaşılamadı ya da cevap geçersizdi |

Diğer tüm kodlar Payment'tan gelir (bkz. [Bölüm 9](#9-karar-motoru-kontroller-irc-ve-f39)).

#### Örnek: 400 cevabı
```json
// istek
{ "mti": "0200", "F2": "4111-1111", "F3": "000000", "F13": "1016", "F49": "TRY" }

// cevap (400)
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "F2":  ["F2: Contains characters that are not allowed for Numeric."],
    "F4":  ["F4 is required."],
    "F7":  ["F7 is required."],
    "F11": ["F11 is required."],
    "F12": ["F12 is required."],
    "F14": ["F14 is required."],
    "F18": ["F18 is required."],
    "F22": ["F22 is required."],
    "F41": ["F41 is required."],
    "F42": ["F42 is required."],
    "F43": ["F43 is required."],
    "F49": ["F49: Contains characters that are not allowed for Numeric."],
    "F13": ["F13: Must be a date in yyyymmdd format, e.g. 20261002."]
  }
}
```

#### İşlem türlerine göre örnekler

**Satış (TL, çip)** — `F3 000000`
```json
{ "mti": "0200", "F2": "4111111111111111", "F3": "000000", "F4": 15000, "F49": "949",
  "F7": "1016095959", "F11": "000001", "F12": "125959", "F13": "20261016",
  "F41": "TERM0001", "F42": "TRENDYOL       ", "F14": "2812", "F18": "5399", "F22": "051",
  "F43": "TRENDYOL                 ISTANBUL     TR" }
```
Bakiye düşer, kartın `LastTransactionDate/Amount`'ı güncellenir.

**Döviz satışı** — `F49 840`, `F4 1000` = 10,00 USD. TL karşılığı TCMB döviz satış kuruyla hesaplanıp bakiyeden düşer.
Kur cache'te yoksa / bayatsa `91`, TCMB'nin hiç yayımlamadığı bir para birimiyse (`999`) `12`.

**Temassız satış** — `F22 071`. Kart temassıza kapalıysa `57`; TL karşılığı `ContactlessLimit`'i aşarsa `65`
(gerçek hayatta terminal bu durumda çip + PIN ister).

**E-ticaret satışı** — `F22 812` ve `"cvv": "482"` (kart açılırken dönen değer). `cvv` yoksa ya da yanlışsa `N7`.

**Bakiye sorgu** — `F3 310000`, `F4 0`. Kart, işyeri ve hesap kontrolleri yapılır, onaylanırsa `00`.
Para hareketi yoktur. (Bakiyenin cevapta F54 ile dönmesi henüz yok.)

**Karttan karta transfer** — `F3 400000`, `F48` = alıcı kart no
```json
{ "mti": "0200", "F2": "4111111111111111", "F3": "400000", "F4": 15000, "F49": "949",
  "F7": "1016100300", "F11": "000005", "F12": "130300", "F13": "20261016",
  "F41": "TERM0001", "F42": "TRENDYOL       ", "F14": "2812", "F18": "5399", "F22": "051",
  "F43": "TRENDYOL                 ISTANBUL     TR", "F48": "5406680000000002" }
```
Gönderenden düşülür, alıcıya eklenir — ikisi aynı SQL transaction'ında. Dövizle de olabilir: iki tarafa da aynı TL karşılığı yansır.

### `GET /openapi/v1.json` ve `/swagger`
Gate'in OpenAPI şeması (Postman'a import edilebilir) ve Swagger UI. `Swagger:Enabled` ile açılır, bkz. [Swagger](#swagger-api-dokümanı-ve-deneme-arayüzü).

---

## 7. Endpoint'ler — Payment yönetim API'si

Base URL: `http://localhost:5002`

Bu API **işlem yapmaz**; işlemlerin ihtiyaç duyduğu veriyi (hesap, kart, işyeri, tanımlar) yönetir. İşlemler Payment'a
HTTP ile değil, sadece RabbitMQ kuyruğundan gelir.

**Katmanlar** (her kaynak için aynı):
```
Controller (Controllers/)   sadece HTTP: sonucu 200/201/204/400/404/409'a çevirir
   └─► Handler (Handlers/)   iş kuralları + loglama; gerekirse cache'i boşaltır
         └─► Repository (Data/)   Dapper ile düz SQL
```

**Ortak kurallar**

| Durum | HTTP |
|---|---|
| Liste / tekil okuma | `200` |
| Oluşturma başarılı | `201 Created` + `Location` header'ı + oluşan kayıt |
| Güncelleme / silme başarılı | `204 No Content` |
| Model doğrulaması (DataAnnotations) geçmedi | `400 ValidationProblem` |
| Kayıt yok | `404` |
| Aynı anahtarla `POST` | `409 Conflict` (gövde: düz metin, örn. `"Account 1000000001 already exists."`) |

- `PUT`'ta anahtar her zaman **URL'den** alınır; gövdedeki anahtar değişikliği yapmaz. Ama gövde yine de modelin doğrulamasından geçmelidir (örn. `accountNo` geçerli aralıkta olmalı).
- "Var mı?" diye önce sorup sonra eklemek yerine DB'nin unique hatası (2627/2601) yakalanır; eşzamanlı iki `POST`'ta da doğru `409` döner.
- Tablolar arasında FK yoktur (sadece `CardSecret → Card`); örneğin olmayan bir hesaba kart açılabilir, işlem sırasında `62` alır.

---

### 7.1 Banka hesapları — `/api/bank-accounts`

Model:
```json
{ "accountNo": 1000000001, "accountStatus": "Active", "balance": 1000.00, "email": "musteri@example.com" }
```
| Alan | Kural |
|---|---|
| `accountNo` | 10 haneli **sayı** (`1000000000`–`9999999999`), JSON'da string değil |
| `accountStatus` | zorunlu, ≤20 karakter. İşlem için `Active` olmalı (`Blocked`, `Closed` … red sebebidir) |
| `balance` | TL, zorunlu (`0` olabilir; gönderilmezse `400`). Sadece `POST`'ta işlenir, `PUT`'ta yok sayılır |
| `email` | zorunlu, geçerli e-posta. Onaylı satış/transferde bildirim buraya gider |

| Metot | Route | Açıklama | Cevaplar |
|---|---|---|---|
| `GET` | `/api/bank-accounts` | Tüm hesaplar | `200 [ ... ]` |
| `GET` | `/api/bank-accounts/{accountNo}` | Tek hesap | `200`, `404` (sayısal olmayan no da `404`) |
| `POST` | `/api/bank-accounts` | Hesap aç (başlangıç bakiyesiyle) | `201`, `400`, `409` |
| `PUT` | `/api/bank-accounts/{accountNo}` | **Sadece** `accountStatus` ve `email` değişir. Gövdedeki `balance` yok sayılır; `email` zorunlu | `204`, `400`, `404` |
| `DELETE` | `/api/bank-accounts/{accountNo}` | Hesabı sil | `204`, `404` |

> Bakiye neden PUT ile değişmiyor? Gerçek bir bankada bakiye sadece işlemlerle (kayıt bırakarak) değişir; doğrudan üzerine yazılamaz.

---

### 7.2 Kartlar — `/api/cards`

Model:
```json
{ "id": 1, "cardNumber": "4111111111111111", "accountNo": 1000000001, "cardType": "Debit", "expiryDate": "1228",
  "lastTransactionDate": null, "lastTransactionAmount": null, "contactlessAllowed": true, "contactlessLimit": 750.00 }
```
| Alan | Kural |
|---|---|
| `id` | DB üretir (IDENTITY) |
| `cardNumber` | zorunlu, ≤19, benzersiz. **Değiştirilemez** |
| `accountNo` | 10 haneli sayı; kartın bağlı olduğu hesap |
| `cardType` | zorunlu, ≤20 karakter (`Debit`, `Credit` …) |
| `expiryDate` | zorunlu, `MMYY`, tam 4 karakter (ISO F14 ise `YYMM` — dikkat!) |
| `contactlessAllowed` | zorunlu (gönderilmezse `400`); temassız işlem izni |
| `contactlessLimit` | TL. `contactlessAllowed: true` ise zorunlu, `false` ise boş olmalı |
| `lastTransactionDate/Amount` | sadece işlem akışı yazar; CRUD ile yazılamaz |

| Metot | Route | Açıklama | Cevaplar |
|---|---|---|---|
| `GET` | `/api/cards` | Tüm kartlar | `200` |
| `GET` | `/api/cards/{id}` | ID ile | `200`, `404` |
| `GET` | `/api/cards/by-number/{cardNumber}` | Kart numarasıyla | `200`, `404` |
| `POST` | `/api/cards` | Kart çıkar. CVV2 (3 hane) ve PIN (4 hane) **rastgele üretilir**, cevapta **bir kez** `cvv` ve `pin` olarak döner | `201`, `400`, `409` |
| `PUT` | `/api/cards/{id}` | Tip, son kullanma, hesap, temassız ayarları değişir. Farklı `cardNumber` gönderilirse `400` | `204`, `400`, `404` |
| `DELETE` | `/api/cards/{id}` | Kartı (ve `CardSecret` satırını, cascade) sil | `204`, `404` |

> Kart numarası neden değiştirilemez? CVV/PIN hash'i kart numarasıyla birlikte hesaplanır (bkz. [11.4](#114-cvv2-ve-pin-güvenliği)). Numara değişirse
> hash'ler anlamsızlaşır; gerçek bankalarda da yeni numara = yeni kart.

---

### 7.3 İşyerleri — `/api/merchants`

Model:
```json
{ "merchantId": "TRENDYOL", "name": "TRENDYOL", "merchantType": "5399", "status": "Active", "createdAt": "2026-10-16T09:00:00" }
```
| Alan | Kural |
|---|---|
| `merchantId` | ≤15; F42 ile karşılaştırılır. `POST`'ta sondaki boşluklar silinerek kaydedilir |
| `name` | zorunlu, ≤100 |
| `merchantType` | MCC, tam 4 rakam |
| `status` | işlem için `Active` olmalı |
| `createdAt` | DB yazar; gövdedeki değer yok sayılır |

| Metot | Route | Açıklama | Cevaplar |
|---|---|---|---|
| `GET` | `/api/merchants` | Tüm işyerleri | `200` |
| `GET` | `/api/merchants/{merchantId}` | Tek işyeri | `200`, `404` |
| `POST` | `/api/merchants` | İşyeri ekle | `201`, `400`, `409` |
| `PUT` | `/api/merchants/{merchantId}` | `name`, `merchantType`, `status` değişir | `204`, `400`, `404` |
| `DELETE` | `/api/merchants/{merchantId}` | Sil | `204`, `404` |

Seed: `IYZICO` (7399), `TRENDYOL` (5399), `GETIR` (5411), `NETFLIX` (4899). Transfer için bir kanal seed'de yoktur;
lab'da `MOBILEBANK` (4829) eklenir ([4.1](#41-veriyi-oluştur)). İşlem akışı işyerini cache'ten değil kendi
SQL transaction'ında okur; değişiklik **bir sonraki işlemde** geçerlidir.

---

### 7.4 İşlem tipleri — `/api/transaction-types`

`(Otc, Ots)` çiftinin adı. İşlem akışı adı (`Sale`, `MoneyTransfer`, `BalanceInquiry`) kullanarak dallanır.

```json
{ "otc": "10", "ots": "11", "name": "Sale" }
```

| Metot | Route | Açıklama | Cevaplar |
|---|---|---|---|
| `GET` | `/api/transaction-types` | Hepsi | `200` |
| `GET` | `/api/transaction-types/{otc}/{ots}` | Tek tip | `200`, `404` |
| `POST` | `/api/transaction-types` | Ekle | `201`, `400`, `409` |
| `PUT` | `/api/transaction-types/{otc}/{ots}` | Sadece `name` değişir | `204`, `400`, `404` |
| `DELETE` | `/api/transaction-types/{otc}/{ots}` | Sil | `204`, `404` |

Seed: `10/11 Sale`, `11/11 MoneyTransfer`, `40/10 BalanceInquiry`. Kodun tanımadığı bir isim eklenirse (örn. `Refund`)
işlemler `1003 → 12` alır. Başarılı her yazma işlemi **tanım cache'ini hemen boşaltır**.

---

### 7.5 MTI + işlem kodu eşlemesi — `/api/mti-processing-codes`

Gelen `MTI + F3`'ün hangi `(Otc, Ots)`'ye gittiği.

```json
{ "mti": "0200", "f3_ProcessingCode": "000000", "otc": "10", "ots": "11" }
```

| Metot | Route | Açıklama | Cevaplar |
|---|---|---|---|
| `GET` | `/api/mti-processing-codes` | Hepsi | `200` |
| `GET` | `/api/mti-processing-codes/{mti}/{processingCode}` | Tek eşleme | `200`, `404` |
| `POST` | `/api/mti-processing-codes` | Ekle | `201`, `400`, `409` |
| `PUT` | `/api/mti-processing-codes/{mti}/{processingCode}` | Sadece hedef `otc`/`ots` değişir | `204`, `400`, `404` |
| `DELETE` | `/api/mti-processing-codes/{mti}/{processingCode}` | Sil | `204`, `404` |

Seed: `0200/000000 → 10/11`, `0200/400000 → 11/11`, `0200/310000 → 40/10`. Tanımsız bir `MTI + F3` → `1001 → 12`.
Bu da tanım cache'ini hemen boşaltır.

> **Neden iki tablo?** `MtiProcessingCode` dış dünyanın diliyle (ISO) iç dünyanın diliyle (Otc/Ots) arasındaki çeviridir.
> Örneğin farklı bir MTI/F3 kombinasyonunu mevcut "Sale" tipine bağlamak için kod değiştirmeden bir satır eklemek yeter.

---

### 7.6 Döviz kurları — `/api/exchange-rates` (salt okunur)

| Metot | Route | Açıklama | Cevaplar |
|---|---|---|---|
| `GET` | `/api/exchange-rates` | Cache'teki tüm kurlar (cache boşsa `[]`) | `200` |
| `GET` | `/api/exchange-rates/{code}` | Sayısal (`840`) ya da harf (`USD`) kod | `200`, `404` (cache'te yok) |

```json
{ "currencyCode": "840", "currency": "USD", "sellingRate": 41.8652, "bulletinDate": "2026-10-16",
  "fetchedAt": "2026-10-16T09:00:01+00:00", "isStale": false }
```
Bu endpoint TCMB'ye **gitmez**, sadece cache'i gösterir. `isStale: true` ise işlem akışı bu kuru kullanmaz (`91`).
Desteklenen kodlar `ExchangeRateCache.IsoToTcmb`'de (USD, EUR, GBP, CHF, JPY, …).

### `GET /openapi/v1.json` ve `/swagger`
Payment'ın OpenAPI şeması ve Swagger UI. `Swagger:Enabled` ile açılır, bkz. [Swagger](#swagger-api-dokümanı-ve-deneme-arayüzü).

> `EntryMode` ve `InternalResponseCode` tablolarının API'si yoktur; doğrudan SQL ile değiştirilir ve en geç 10 dk içinde devreye girer.

---

## 8. Servisler arası sözleşmeler

### 8.1 Gate → Payment (RabbitMQ)

| | |
|---|---|
| Kuyruk | `payment.transactions` (durable; iki taraf da aynı ayarlarla tanımlar, hangisi önce açılırsa açılsın hazır olur) |
| İstek property'leri | `CorrelationId`, `ReplyTo=amq.rabbitmq.reply-to`, `Expiration` (ms), `Timestamp` (sn), `ContentType=application/json` |
| Cevap | isteğin `ReplyTo`'suna, aynı `CorrelationId` ile |

İstek gövdesi (camelCase):
```json
{ "mti": "0200", "cardNumber": "4111111111111111", "processingCode": "000000", "amountMinor": 15000,
  "currencyCode": "949", "transmissionDateTime": "1016095959", "stan": "000001", "localTime": "125959",
  "localDate": "20261016", "terminalId": "TERM0001", "cardExpiry": "2812", "merchantType": "5399",
  "entryMode": "051", "rrn": null, "merchantId": "TRENDYOL       ", "cardAcceptor": "TRENDYOL                 ISTANBUL     TR",
  "recipientCardNumber": null, "cvv": null }
```
Cevap gövdesi:
```json
{ "responseCode": "00" }
```
Payment'ın teknik cevapları: gövde okunamadı / geçersiz → `30`; DB'ye ulaşılamadı vb. → `91`; IRC'nin F39 karşılığı tanımsız → `96`.

### 8.2 Payment → Kafka

| | |
|---|---|
| Topic | `payment.transaction-events` (3 partition) |
| Key | hesap no → aynı hesabın olayları aynı partition'da, sırası korunur |
| Ne zaman | **commit edilen her karar** (onay ve red) |
| Ne zaman değil | tekrar gelen istek (ilk istek zaten yazdı), kayıt açılmayan red (`1001`), teknik hata (`91`) |

```json
{ "transactionId": "…", "occurredAt": "2026-10-16T10:00:00+00:00", "mti": "0200", "transactionType": "Sale",
  "responseCode": "00", "irc": "0000", "maskedCardNumber": "411111********11", "accountNo": 1000000001,
  "email": "musteri@example.com", "amount": 10.00, "currencyCode": "840", "localAmount": 418.65,
  "cardAcceptor": "PAYMENT SYSTEM POS SIMULATOR", "transactionDate": "20261016", "localTime": "125959",
  "terminalId": "TERM0001", "stan": "000001",
  "recipientMaskedCardNumber": null, "recipientAccountNo": null, "recipientEmail": null,
  "eventType": "TransactionCompleted", "version": 1 }
```
- Kart numarası sadece **maskeli** taşınır.
- `recipient*` alanları sadece transferde (alıcı kart bulunduysa) doludur.
- Alan eklemek geriye uyumludur; alan silmek / anlamını değiştirmek `version`'ı artırır.
- Kafka kapalıysa olay 30 sn denenir, sonra loglanıp bırakılır; işlem etkilenmez (outbox yok — bilinçli tercih).

---

## 9. Karar motoru: kontroller, IRC ve F39

`TransactionHandler.DecideAsync` kontrolleri **bu sırayla** yapar; ilk başarısız olan kararı verir. F39 değerleri
`InternalResponseCode` tablosunun seed değerleridir.

> **Sıra neden önemli?** Kontroller ucuzdan pahalıya ve dıştan içe dizilidir: önce işyeri, sonra kart, sonra hesap, en son para.
> Bu yüzden yanlış MCC'li bir istekte logda `account -` görürsün: işlem işyeri aşamasında reddedildi, kart ve hesap hiç okunmadı.
> Hangi satırın tetiklendiğini F39'tan değil, logdaki IRC'den anlarsın (bkz. [4.4](#44-red-senaryoları-sistemi-bilerek-reddettir)).

| # | Kontrol | Hangi işlemde | IRC | F39 |
|---|---|---|---|---|
| 0 | `MTI + F3` tanımsız (kayıt açılmaz) | hepsi | `1001` | `12` |
| 1 | F22'nin ilk iki hanesi `EntryMode`'da yok | hepsi | `1002` | `12` |
| 2 | F42 yok (Gate zaten `400` döner; savunma) | hepsi | `1007` | `30` |
| 3 | İşyeri `Merchant` tablosunda yok | hepsi | `4001` | `03` |
| 4 | İşyeri `Active` değil | hepsi | `4002` | `03` |
| 5 | F18 işyerinin MCC'si değil | hepsi | `4003` | `03` |
| 6 | Kart yok | hepsi | `2001` | `14` |
| 7 | Kartın süresi geçmiş ya da F14 kartla uyuşmuyor | hepsi | `2002` | `54` |
| 8 | Kart fiziksel yok (`01x`/`81x`) ve CVV2 yok; ya da CVV2 geldi ama tutmuyor | hepsi | `2006` | `N7` |
| 9 | Hesap yok ya da `Active` değil | hepsi | `2003` | `62` |
| — | **Bakiye sorgu** buraya kadar geçtiyse → onay | `BalanceInquiry` | `0000` | `00` |
| 10 | F48 yok ya da kart numarası değil | transfer | `1005` | `30` |
| 11 | Alıcı kart yok | transfer | `2007` | `14` |
| 12 | Alıcı kart gönderenle aynı hesapta | transfer | `1006` | `12` |
| 13 | Alıcının hesabı yok ya da `Active` değil | transfer | `2008` | `62` |
| 14 | TCMB bu para birimi için kur yayımlamıyor (örn. `999`) | satış, transfer | `1004` | `12` |
| 15 | Döviz ama cache'te kur yok ya da bayat | satış, transfer | `9001` | `91` |
| 16 | Tutar 0 | satış, transfer | `3001` | `13` |
| 17 | Temassız ama kart temassıza kapalı | satış | `2004` | `57` |
| 18 | Temassız limit (TL karşılık) aşıldı | satış | `2005` | `65` |
| 19 | Bakiye yetersiz | satış, transfer | `3002` | `51` |
| ✔ | **Onay**: bakiye düşer (transferde alıcıya eklenir), kartın `LastTransaction*`'ı güncellenir | satış, transfer | `0000` | `00` |
| — | Tip tanımlı ama kod onu işlemiyor (örn. `Refund`) | — | `1003` | `12` |

Ayrıca: IRC tabloda yoksa → `96` (işlem asla tahminle onaylanmaz). Beklenmeyen hata (DB yok vb.) → `91`, commit edilmediği için para hareketi yok.

**Neden IRC + tablo?** İş sebebi (bakiye yetersiz) ile dış dünyaya gösterilen kod (`51`) ayrıdır. Bir şemanın ya da kartın
markanın istediği kod değişirse SQL ile bir satır güncellenir, kod değişmez. Teknik kodlar (`30`, `91`, `96`) ise sabittir:
DB'ye ulaşılamadığında tablo da okunamaz.

---

## 10. Veritabanı

Script'ler: `docker/sql/init/01_create_tables.sql` (şema) ve `02`–`05` (seed). Şema EF migration'larıyla değil bu script'lerle
yönetilir; erişim **Dapper** ile düz SQL'dir.

| Tablo | PK | İçerik |
|---|---|---|
| `BankAccount` | `AccountNo` (BIGINT, 10 hane) | Durum, bakiye (TL), e-posta |
| `Card` | `Id` (IDENTITY), `CardNumber` UNIQUE | Hesap no, tip, `ExpiryDate` (MMYY), temassız ayarları, son işlem |
| `CardSecret` | `CardNumber` (FK → Card, ON DELETE CASCADE) | `CvvHash`, `PinHash` (HMAC-SHA256) |
| `Merchant` | `MerchantId` | Ad, MCC, durum |
| `TransactionType` | `(Otc, Ots)` | İşlem adı |
| `MtiProcessingCode` | `(Mti, F3_ProcessingCode)` | → `(Otc, Ots)` |
| `EntryMode` | `Code` (F22'nin ilk 2 hanesi) | Ad, `IsContactless` |
| `InternalResponseCode` | `Irc` | Açıklama, `F39_ResponseCode` |
| `DebitTransaction` | `Guid` (UUID v5) | Her işlemin kaydı; kolonlar ISO alan numarasını taşır |

`DebitTransaction` kolonları: `Mti`, `F2_CardNo`, `F3_ProcessingCode`, `Otc`, `Ots`, `F4_Amount` (orijinal para birimi),
`F49_CurrencyCode`, `LocalTxnAmount` (TL karşılık), `F12_TransactionTime`, `F13_TransactionDate` (yyyymmdd), `F14_CardExpiry`,
`F18_MerchantCode`, `F22_EntryMode`, `F39_ResponseCode`, `F42_MerchantId`, `F43_Description`, `F48_RecipientCardNo`, `CreatedAt`, `UpdatedAt`.

Seed `EntryMode`: `00` Unknown · `01` Manual · `02` MagneticStripe · `05` Chip · `07` ContactlessChip ✱ · `81` ECommerce ·
`90` MagneticStripeFullTrack · `91` ContactlessMagneticStripe ✱ (✱ = temassız).

Kontrol için:
```bash
docker exec -it payment_system_db /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P '<şifre>' -C -d PaymentSystem \
  -Q "SELECT TOP 10 Guid, F2_CardNo, F3_ProcessingCode, F4_Amount, F39_ResponseCode FROM dbo.DebitTransaction ORDER BY CreatedAt DESC"
```

### 10.1 Boş kalabilen kolonlar
Kural: **boş kolonlu kayıt açılmaz.** Gate, DB'ye yazılan her ISO alanını zorunlu tutar; Payment'ın yönetim API'si hesap ve
kartta her alanı ister. Şema `NULL`'a izin verse de uygulama vermez. Sadece doğası gereği boş olması gereken kolonlar boş kalır:

| Tablo | Kolon | Ne zaman boş | Neden |
|---|---|---|---|
| `Card` | `LastTransactionDate`, `LastTransactionAmount` | Kart hiç onaylı işlem yapmadıysa | İşlem akışı yazar. Transferde sadece **gönderen** kartınki güncellenir |
| `Card` | `ContactlessLimit` | `ContactlessAllowed = 0` ise (boş **olmalı**) | Temassıza kapalı kartta limit anlamsız |
| `DebitTransaction` | `F48_RecipientCardNo` | Satış ve bakiye sorguda; transferde F48 kart no değilse (`1005`) | Sadece transferin alıcısı |
| `DebitTransaction` | `LocalTxnAmount` | Döviz kuru bulunamadıysa (işlem `91`/`12` ile reddedilir) | TL karşılığı hesaplanamadı |

Kontrolün yeri: `Gate/TransactionHandler.RequiredFinancialFields` (işlem), `Payment/Models/BankAccount.cs` ve `Card.cs`
(`[Required]`, `[JsonRequired]`, `Card.Validate`). Değer tipli alanlarda (`balance`, `contactlessAllowed`) `[Required]` işe
yaramaz — body'de yoksa C# varsayılanı (`0`, `false`) sessizce yazılırdı; `[JsonRequired]` alanın body'de **bulunmasını** ister.

---

## 11. Önemli tasarım konuları

### 11.1 Idempotency (aynı istek iki kez gelirse)
POS cevabı alamazsa aynı isteği tekrar gönderir. Para iki kez düşmemeli.

- Kaydın PK'sı rastgele değil, isteğin `MTI | F2 | F3 | F4 | F13 | F12` alanlarından üretilen bir **UUID v5**'tir (sabit namespace + SHA-1).
- Tekrar gelen istek aynı Guid'i üretir → `INSERT` PK'ya takılır → transaction rollback → ilk kararın F39'u okunup döner.
- Aynı anda gelirlerse ikincisi ilkinin transaction'ı bitene kadar bekler, sonra çakışır; asla iki kez karara bağlanmaz.
- Şemaya kolon eklenmeden çözüldü. **Sınır**: aynı kartla aynı saniyede aynı tutarda iki *gerçek* işlem, ikincisi tekrar sayılır.

### 11.2 Zaman aşımı ve "geç işlenme" koruması
Sorun: Gate 10 sn bekleyip `91` döndü, ama Payment isteği sonradan işlerse müşteri "olmadı" sanar, parası düşer.

- İstek kuyruğa `Expiration` (TTL) ile girer: süresi dolarsa RabbitMQ siler.
- Payment kuyruktan almış olsa bile `Timestamp + Expiration`'a bakar; süresi dolmuşsa işlemez.
- Gate'e geç gelen cevap loglanıp atılır.
- **Sınır**: Payment işlemi tam süre dolarken bitirirse para düşer ama Gate `91` döner. Çözümü reversal (`0400`) — sonraki adım.

### 11.3 Eşzamanlılık
- Bakiye kontrolü ve düşme **tek SQL**'dedir: `UPDATE ... SET Balance = Balance - @amount WHERE AccountNo = @no AND Balance >= @amount`.
  Etkilenen satır 0 ise yetersiz bakiye. İki eşzamanlı satış hesabı eksiye düşüremez.
- Transferde iki hesabın satırı önce **PK sırasıyla** kilitlenir (`UPDLOCK`); A→B ve B→A aynı anda gelse de deadlock olmaz.
- Kayıt + bakiye + kart güncellemesi tek transaction'dadır: ya hepsi ya hiçbiri.

### 11.4 CVV2 ve PIN güvenliği
```
Kart açılırken:  rastgele 482 / 0731 ──HMAC-SHA256(CARD_SECRET_KEY, "PAN:cvv:482" / "PAN:pin:0731")──► CardSecret
İşlemde:         gelen cvv ─────────HMAC-SHA256(CARD_SECRET_KEY, "PAN:cvv:<gelen>")───────────────► == CvvHash ? (sabit zamanlı)
```
- Neden düz SHA-256 değil de HMAC? CVV'nin sadece 1000 olası değeri var; anahtarsız hash saniyeler içinde kırılır. Anahtar DB'de değil ortam değişkeninde.
- Kart numarası hash'e girer: aynı CVV'li iki kartın hash'i farklıdır.
- Karşılaştırma `CryptographicOperations.FixedTimeEquals` ile yapılır (timing saldırısına karşı).
- CVV kart fiziksel yokken zorunlu (`01x`, `81x`); çip/temassızda istenmez (kart kendi kriptogramını üretir) ama gelirse kontrol edilir.
- PIN şimdilik sadece saklanır, işlemde doğrulanmaz (ileride F52 PIN block).
- `CARD_SECRET_KEY` değişirse **tüm kartların** CVV/PIN hash'i geçersiz olur. Gerçek bankalarda bu anahtar HSM'dedir; `ICardSecretHasher` arayüzü bu geçişi kolaylaştırır.
- Düz CVV/PIN hiçbir yere yazılmaz: DB, log, Kafka.

### 11.5 Tanım tabloları cache'i — `Parameters/ParameterCache.cs`
`TransactionType`, `MtiProcessingCode`, `EntryMode`, `InternalResponseCode` her işlemde DB'den okunmaz; dördü birden `IMemoryCache`'e yüklenir.
- 10 dakikada bir (`Parameters:RefreshMinutes`) yenilenir.
- `/api/transaction-types` ve `/api/mti-processing-codes` üzerinden yapılan değişiklik cache'i **hemen** boşaltır.
- Birden fazla Payment instance'ı olursa diğerleri değişikliği en geç 10 dk içinde görür.

### 11.6 Döviz kurları — `ExchangeRates/`
```
TCMB today.xml ──(açılışta + saatte bir)──► ExchangeRateRefresher ──yazar──► IMemoryCache ("fx:USD" ...)
                                                                                  │ sadece okur
                                                     TransactionHandler: TL = tutar × ForexSelling / Unit (kuruşa, yarım yukarı)
```
- İşlem akışı TCMB'ye **hiç** gitmez; satışın hızı dış servise bağlı değildir.
- Çekim başarısızsa eski kurlar kalır, 1 dk sonra tekrar denenir.
- Son başarılı çekimden 24 saat geçerse kur bayat sayılır → `91`.
- Cache in-memory'dir: Payment yeniden başlayınca ilk çekime kadar döviz işlemleri `91` alır, TL işlemler etkilenmez.

### 11.7 PCI ve loglama
- `PanMask`: ilk 6 + son 2 hane görünür. F2 ve F48 loglarda, exception mesajlarında ve Kafka olaylarında daima maskelidir.
- Payment geçersiz bir mesajın ham gövdesini loglamaz (içinde açık PAN olabilir).
- `IsoLog` iki serviste de aynı log biçimini üretir, böylece bir işlem STAN ile uçtan uca izlenir.

---

## 12. Notification servisi

.NET 9 worker servisi (HTTP yok). `payment.transaction-events`'i `notification` consumer group'uyla okur.

**Kime e-posta gider?**
- Sadece **onaylı** (`F39 00`) olaylar. Red, bakiye sorgu ve bilinmeyen olay tipleri atlanır.
- **Satış**: kartın hesabının e-postasına ("Kartınızla … tutarında satış yapıldı").
- **Transfer**: gönderene ("… numaralı karta gönderildi") **ve** alıcıya ("… numaralı karttan geldi"), iki ayrı e-posta.
- Hesabın e-postası yoksa loglanıp geçilir.

**Güvenilirlik**
- **At-least-once**: offset olay işlendikten *sonra* saklanır; servis ortada kapanırsa olay tekrar gelir.
- **Tekrar koruması**: son 10.000 gönderilen `(işlem Guid'i, kişi)` çifti bellekte tutulur; aynı kişiye ikinci e-posta gitmez.
  Transferde bir tarafın hatası diğerini engellemez.
- **Yeniden deneme**: 3 deneme, 5 sn arayla; sonra loglanıp geçilir — tüketici tek bir olayda takılı kalmaz.
- Bozuk JSON olay atlanır.
- Açılışta topic yoksa oluşturur; group ilk kez açılıyorsa topic'in başından okur.

**Gmail ile gerçek gönderim**
1. Google hesabında 2 adımlı doğrulamayı aç.
2. https://myaccount.google.com/apppasswords → 16 haneli uygulama şifresi oluştur.
3. `.env`: `SMTP_USER=gonderen@gmail.com`, `SMTP_PASSWORD=abcdefghijklmnop` → `docker compose up -d --build notification`
4. Hesaba e-posta tanımla (`PUT /api/bank-accounts/{no}`), o hesabın kartıyla satış yap.

Gmail dışı için `SMTP_HOST`, `SMTP_PORT`, `SMTP_FROM`. `SMTP_USER` ve `SMTP_FROM`'un ikisi de boşsa servis açılışta hata verip durur.

---

## 13. Ayarlar ve ortam değişkenleri

`appsettings.json` bölümleri ortam değişkeniyle ezilebilir: `Bolum__Anahtar` (örn. `Gate__PaymentTimeoutSeconds=5`).
Şifreler `appsettings.json`'da **durmaz**, sadece ortam değişkeninden gelir.

| Servis | Anahtar | Varsayılan | Açıklama |
|---|---|---|---|
| Gate | `Gate:RabbitMqUri` | `amqp://payment@localhost:5672` | Şifresiz URI |
| Gate | `Gate:PaymentTimeoutSeconds` | `10` | Cevap bekleme süresi = mesaj TTL'i |
| Gate, Payment | `RABBITMQ_PASSWORD` | — | zorunlu |
| Gate, Payment | `Swagger:Enabled` | `true` | `/openapi/v1.json` ve `/swagger` açık mı |
| Payment | `ConnectionStrings:PaymentSystem` | `Server=localhost,1433;Database=PaymentSystem;...` | Şifresiz |
| Payment | `MSSQL_SA_PASSWORD` | — | zorunlu |
| Payment | `ConnectionStrings:RabbitMq` | `amqp://payment@localhost:5672` | |
| Payment | `CARD_SECRET_KEY` | — | zorunlu, 64 hex |
| Payment | `ExchangeRates:TcmbUrl` / `RefreshMinutes` / `RetryMinutes` / `MaxAgeHours` | TCMB today.xml / `60` / `1` / `24` | |
| Payment | `Parameters:RefreshMinutes` | `10` | Tanım cache'i ömrü |
| Payment | `Kafka:BootstrapServers` / `Topic` / `MessageTimeoutMs` | `localhost:9094` / `payment.transaction-events` / `30000` | |
| Notification | `Kafka:BootstrapServers` / `Topic` / `GroupId` | `localhost:9094` / `payment.transaction-events` / `notification` | |
| Notification | `Smtp:Host` / `Port` / `Security` | `smtp.gmail.com` / `587` / `StartTls` | `Security`: `StartTls`, `SslOnConnect`, `None` |
| Notification | `Smtp:User` / `Password` / `From` / `FromName` | — | |
| Notification | `Smtp:MaxAttempts` / `RetryDelaySeconds` | `3` / `5` | |

---

## 14. Testler, CI ve branch akışı

| Test projesi | Neyi test eder |
|---|---|
| `tests/PaymentSystem.Gate.Tests` | Endpoint alan doğrulaması, Gate karar mantığı (sahte Payment), gerçek RabbitMQ üzerinden request-reply (eşzamanlılık, geçersiz cevap, timeout + TTL, geç cevap), ISO8583 pack/unpack ve framing |
| `tests/PaymentSystem.Payment.Tests` | İşlem akışı (idempotency, eşzamanlılık, tüm red kodları, döviz), kuyruk tüketicisi (gerçek RabbitMQ + SQL), 5 CRUD controller + repository'ler, model doğrulama, TCMB ayrıştırma + kur cache'i, parametre cache'i, Kafka olayları |
| `tests/PaymentSystem.Notification.Tests` | Bildirim kuralları, e-posta içerikleri, gerçek Kafka'dan okuma, SMTP gönderimi (Mailpit container'ına; gerçek e-posta gitmez) |

Testler [Testcontainers](https://dotnet.testcontainers.org/) ile **gerçek** SQL Server, RabbitMQ, Kafka ve Mailpit başlatır;
SQL Server'da `docker/sql/init/*.sql` çalıştırılır, yani testler compose'daki şemanın aynısına karşı koşar. Docker Desktop açık olmalı.

```bash
dotnet test -p:CollectCoverage=true
```
Her test projesinde satır coverage'ı **%80**'in altındaysa komut başarısız olur (`Program.cs` hariç). Rapor: `tests/*/TestResults/coverage.cobertura.xml`.

**Branch akışı**: geliştirme `develop`'ta yapılır; `develop → master` pull request'i açılır. `.github/workflows/ci.yml`
her PR'da ve `master` push'unda restore → build → test + coverage kapısını çalıştırır. `master` korumalıdır: `build-and-test` geçmeden merge edilemez.

---

## 15. Kod haritası

```
payment_system/
├─ docker-compose.yml               tüm servisler + altyapı
├─ .env.example                     şifreler ve anahtarlar şablonu
├─ docker/sql/
│  ├─ init/01_create_tables.sql     şema
│  ├─ init/02..05_seed_*.sql        tanım tabloları ve işyerleri
│  └─ run-init.sh                   db-init container'ının script'i
├─ src/
│  ├─ PaymentSystem.Iso8583/        ortak kütüphane
│  │  ├─ Iso87Fields.cs             desteklenen alanlar ve formatları  ← önce bunu oku
│  │  ├─ IsoFieldDefinition.cs      Validate(): uzunluk + karakter seti
│  │  ├─ IsoMessage.cs              MTI + alanlar, istek→cevap MTI
│  │  ├─ IsoMessagePacker.cs        bitmap ile byte'a çevirme / geri çözme
│  │  ├─ IsoFraming.cs              TCP için 2 baytlık uzunluk başlığı
│  │  ├─ PanMask.cs, IsoLog.cs      maskeleme ve log biçimi
│  ├─ PaymentSystem.Gate/
│  │  ├─ Program.cs                 tek endpoint: POST /api/transactions
│  │  ├─ TransactionEndpoint.cs     JSON → ISO, doğrulama, 400
│  │  ├─ TransactionHandler.cs      0200 mü, zorunlu alanlar, echo
│  │  ├─ Payment/RabbitMqPaymentClient.cs   request-reply, timeout, TTL
│  │  └─ PaymentSystem.Gate.http    örnek işlemler
│  ├─ PaymentSystem.Payment/
│  │  ├─ Program.cs                 DI kurulumu (hepsi singleton)
│  │  ├─ Transactions/              ← sistemin kalbi
│  │  │  ├─ TransactionQueueConsumer.cs   kuyruk → handler → cevap
│  │  │  ├─ TransactionHandler.cs         tüm iş kuralları
│  │  │  ├─ InternalResponseCodes.cs      IRC sabitleri
│  │  │  └─ TransactionException.cs
│  │  ├─ Controllers/ Handlers/ Data/     yönetim CRUD katmanları
│  │  ├─ Data/TransactionRepository.cs    işlem SQL'leri (insert, debit, transfer)
│  │  ├─ Parameters/                tanım tabloları cache'i
│  │  ├─ ExchangeRates/             TCMB istemcisi, refresher, cache
│  │  ├─ Cards/                     CVV/PIN HMAC
│  │  ├─ Events/                    Kafka publisher + olay sözleşmesi
│  │  └─ PaymentSystem.Payment.http örnek yönetim istekleri
│  └─ PaymentSystem.Notification/
│     ├─ Events/TransactionEventConsumer.cs   Kafka okuma, offset yönetimi
│     ├─ TransactionNotifier.cs               kime, ne zaman, tekrar koruması
│     └─ Email/                               içerikler + SMTP (MailKit)
└─ tests/                           her servis için Testcontainers'lı testler
```

---

## 16. Öğrenme yolu

Projeyi sıfırdan anlamak için önerilen okuma sırası:

1. **Çalıştır ve dene** — [Bölüm 4](#4-uygulamalı-lab-sıfırdan-uçtan-uca-test)'teki lab'ı uygula; logları üç pencerede izle.
   Her redde önce F39'a, sonra logdaki IRC'ye bak ve [Bölüm 9](#9-karar-motoru-kontroller-irc-ve-f39)'daki satırı bul.
2. **ISO8583'ü anla** — `Iso87Fields.cs` → `IsoFieldDefinition.cs` → `IsoMessage.cs` → `IsoMessagePacker.cs` (bitmap mantığı). `IsoMessagePackerTests.cs` örnek mesajları gösterir.
3. **Gate'i takip et** — `Program.cs` → `TransactionEndpoint.HandleAsync` → `TransactionHandler.ProcessAsync` → `RabbitMqPaymentClient.AuthorizeAsync`.
   Soru: Payment kapalıyken ne olur? (`TransactionEndpointTests`, `RabbitMqPaymentClientTests`)
4. **Payment'ın kalbini oku** — `TransactionQueueConsumer.DecideAsync` → `TransactionHandler.AuthorizeAsync` → `DecideAsync` → `SaleAsync` / `TransferAsync`.
   Yanına `TransactionRepository.cs` aç: hangi kontrol hangi SQL'e karşılık geliyor?
5. **Tanım tablolarını kurcala** — `/api/mti-processing-codes`'a yeni bir eşleme ekle, sonuçlarını gözle (`12` → `00`).
6. **Yan konular** — `ParameterCache`, `ExchangeRateRefresher` + `ExchangeRateCache`, `HmacCardSecretHasher`.
7. **Olay tarafı** — `KafkaTransactionEventPublisher` → `TransactionEventConsumer` → `TransactionNotifier`.
8. **Testleri oku** — `TransactionHandlerTests.cs` iş kurallarının en iyi "çalıştırılabilir dokümantasyonu"dur.

Kendine sorabileceğin sorular:
- Aynı satış iki kez gelince DB'de kaç satır oluşuyor? Neden?
- Gate `91` dönerse müşterinin parası düşmüş olabilir mi? Hangi durumda?
- Bir red neden commit edildikten sonra exception olarak fırlatılıyor?
- `EntryMode` tablosuna `07`'yi temassız değil diye güncellersen ne değişir, ne zaman değişir?

---

## 17. Bilinen sınırlar ve sıradaki adımlar

- **Reversal (`0400`)**: timeout'ta Gate'in iptal mesajı bırakması, Payment açılınca işlemesi (store & forward).
- **Cevap sözleşmesini genişletmek**: F38 (onay kodu), F37 (RRN), F54 (bakiye sorgu cevabı).
- **Kimlik doğrulama**: Gate'e API key / mTLS; Payment yönetim endpoint'lerini iç ağa almak.
- **PIN doğrulama** (F52 PIN block).
- **Off-us transfer**: başka bankanın kartına.
- **Bildirim geçmişi**: gönderilen / gönderilemeyen e-postaların kaydı.
- **Şemayı ISO'ya uydurmak**: `DebitTransaction.F13_TransactionDate` şu an `yyyymmdd` (ISO: MMDD), `Card.ExpiryDate` `MMYY` (ISO F14: YYMM).
- **Idempotency anahtarı** STAN/terminal no içermiyor (tabloda kolon yok): aynı saniyede aynı kartla aynı tutarda iki gerçek işlem çakışır.
- **Yeni tüketiciler**: fraud, mutabakat, raporlama `payment.transaction-events`'i kendi consumer group'larıyla okuyabilir.
