using Microsoft.EntityFrameworkCore;
using CostFlow.Models.TiDb;

namespace CostFlow.Data
{
    public class TiDbContext : DbContext
    {
        public TiDbContext(DbContextOptions<TiDbContext> options) : base(options)
        {
        }

        public DbSet<SavedOrderBatch> SavedOrderBatches { get; set; }
        public DbSet<SavedOrderItem> SavedOrderItems { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<SavedOrderBatch>()
                .HasIndex(b => b.BatchName)
                .IsUnique();

            builder.Entity<SavedOrderItem>()
                .HasOne(i => i.Batch)
                .WithMany(b => b.Items)
                .HasForeignKey(i => i.BatchId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
