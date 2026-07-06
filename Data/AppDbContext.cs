using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using CostFlow.Models;

namespace CostFlow.Data
{
    public class AppDbContext : IdentityDbContext<ApplicationUser>
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<ImportSession> ImportSessions { get; set; }
        public DbSet<MergeResult> MergeResults { get; set; }
        public DbSet<ProductPrice> ProductPrices { get; set; }
        public DbSet<SparePartOrderBatch> SparePartOrderBatches { get; set; }
        public DbSet<SparePartOrder> SparePartOrders { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder); // Identity tables

            // Unique index on EmployeeCode
            builder.Entity<ApplicationUser>()
                .HasIndex(u => u.EmployeeCode)
                .IsUnique();
        }
    }
}
