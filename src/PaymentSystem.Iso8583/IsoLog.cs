namespace PaymentSystem.Iso8583;

// Loglarda ISO8583 alanlarının yazılışı: "F2 (Kart No)=411111********11". Gate ve Payment aynı biçimi kullanır,
// böylece bir işlem iki servisin logunda da aynı etiketlerle takip edilir. Kart numaraları (F2, F48) her zaman maskelenir.
public static class IsoLog
{
    // "F11 (STAN)"; Iso87Fields'ta tanımlı olmayan alan sadece numarasıyla yazılır.
    public static string Label(int field) =>
        Iso87Fields.Default.TryGetValue(field, out var definition) ? $"F{field} ({definition.Label})" : $"F{field}";

    // Değer yoksa "F11 (STAN)=-". F43 gibi sağdan boşlukla doldurulan değerler kırpılır.
    public static string Field(int field, string? value) =>
        $"{Label(field)}={(value is null ? "-" : field is 2 or 48 ? PanMask.Mask(value) : value.TrimEnd())}";

    // Mesajda bulunan alanlar numara sırasıyla, boşlukla ayrılmış.
    public static string Fields(IEnumerable<KeyValuePair<int, string?>> fields) =>
        string.Join(' ', fields.Where(f => f.Value is not null).OrderBy(f => f.Key).Select(f => Field(f.Key, f.Value)));

    // Doğrulama hatalarının anahtarları ("F11", "mti", "cvv"): alan numarası olanlar etiketiyle yazılır.
    public static string Keys(IEnumerable<string> keys) =>
        string.Join(", ", keys.Select(k => k.Length > 1 && k[0] == 'F' && int.TryParse(k[1..], out var field) ? Label(field) : k));
}
