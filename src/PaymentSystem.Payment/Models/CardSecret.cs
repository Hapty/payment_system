namespace PaymentSystem.Payment.Models;

// dbo.CardSecret tablosunun bir satırı: kartın CVV2'si ve PIN'i, HMAC'lenmiş olarak (bkz. Cards/HmacCardSecretHasher).
// Düz değerler sadece kart oluşturulurken bir kez döner (IssuedCard); hiçbir endpoint bu tabloyu göstermez.
public sealed record CardSecret(string CardNumber, byte[] CvvHash, byte[] PinHash);
