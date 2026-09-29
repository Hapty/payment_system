using Microsoft.EntityFrameworkCore;
using PaymentSystem.Api.Models;

namespace PaymentSystem.Api.Data;

// Mirrors docker/sql/init/01_create_tables.sql - the schema is owned by those scripts, not by EF migrations.
// DebitTransaction is not mapped yet; it will be added with the ISO8583 transaction flow.
public class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<Card> Cards => Set<Card>();
    public DbSet<TransactionType> TransactionTypes => Set<TransactionType>();
    public DbSet<MtiProcessingCode> MtiProcessingCodes => Set<MtiProcessingCode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BankAccount>(e =>
        {
            e.ToTable("BankAccount");
            e.HasKey(x => x.AccountNo);
            e.Property(x => x.AccountNo).HasColumnType("varchar(34)");
            e.Property(x => x.AccountStatus).HasColumnType("varchar(20)");
            e.Property(x => x.Balance).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<Card>(e =>
        {
            e.ToTable("Card");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CardNumber).IsUnique();
            e.Property(x => x.CardNumber).HasColumnType("varchar(19)");
            e.Property(x => x.AccountNo).HasColumnType("varchar(34)");
            e.Property(x => x.CardType).HasColumnType("varchar(20)");
            e.Property(x => x.ExpiryDate).HasColumnType("char(4)");
            e.Property(x => x.Cvv).HasColumnType("char(3)");
            e.Property(x => x.LastTransactionAmount).HasColumnType("decimal(18,2)");
            e.Property(x => x.ContactlessLimit).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<TransactionType>(e =>
        {
            e.ToTable("TransactionType");
            e.HasKey(x => new { x.Otc, x.Ots });
            e.Property(x => x.Otc).HasColumnType("char(2)");
            e.Property(x => x.Ots).HasColumnType("char(2)");
            e.Property(x => x.Name).HasColumnType("varchar(50)");
        });

        modelBuilder.Entity<MtiProcessingCode>(e =>
        {
            e.ToTable("MtiProcessingCode");
            e.HasKey(x => new { x.Mti, x.F3_ProcessingCode });
            e.Property(x => x.Mti).HasColumnType("char(4)");
            e.Property(x => x.F3_ProcessingCode).HasColumnType("char(6)");
            e.Property(x => x.Otc).HasColumnType("char(2)");
            e.Property(x => x.Ots).HasColumnType("char(2)");
        });
    }
}
