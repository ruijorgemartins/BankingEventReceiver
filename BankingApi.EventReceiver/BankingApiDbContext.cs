using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BankingApi.EventReceiver;

public class BankingApiDbContext(DbContextOptions<BankingApiDbContext> options, IConfiguration config)
    : DbContext(options)
{
    public DbSet<BankAccount> BankAccounts { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlServer(config.GetConnectionString("BankingApiDb"));
}