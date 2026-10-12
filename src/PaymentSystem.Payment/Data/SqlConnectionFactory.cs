using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace PaymentSystem.Payment.Data;

// Her sorgu için yeni bir SqlConnection üretir. Bağlantıyı açıp kapatmak ucuzdur:
// SqlClient bağlantıları arka planda bir havuzda (connection pool) tutar ve yeniden kullanır.
public sealed class SqlConnectionFactory(string connectionString)
{
    static SqlConnectionFactory()
    {
        // Dapper string parametreleri varsayılan olarak nvarchar gönderir. Bizim kolonlarımız varchar/char olduğu için
        // SQL Server her karşılaştırmada kolonu dönüştürmek zorunda kalır ve index'i kullanamayabilir.
        // String'leri varchar (AnsiString) olarak göndermek bunu önler.
        SqlMapper.AddTypeMap(typeof(string), DbType.AnsiString);
    }

    public SqlConnection Create() => new(connectionString);
}
