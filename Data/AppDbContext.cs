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
        
        // New tracking system tables
        public DbSet<Report> Reports { get; set; }
        public DbSet<OrderTrackingMaster> OrderTrackingMasters { get; set; }
        public DbSet<WeeklyPlan> WeeklyPlans { get; set; }
        public DbSet<WeeklyPlanDetail> WeeklyPlanDetails { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder); // Identity tables

            // Unique index on EmployeeCode
            builder.Entity<ApplicationUser>()
                .HasIndex(u => u.EmployeeCode)
                .IsUnique();

            // Unique index on ReportName
            builder.Entity<Report>()
                .HasIndex(r => r.ReportName)
                .IsUnique();

            // Report -> OrderTrackingMaster (1-to-many)
            builder.Entity<OrderTrackingMaster>()
                .HasOne(o => o.Report)
                .WithMany(r => r.Orders)
                .HasForeignKey(o => o.ReportId)
                .OnDelete(DeleteBehavior.Cascade);

            // Report -> WeeklyPlan (1-to-many)
            builder.Entity<WeeklyPlan>()
                .HasOne(w => w.Report)
                .WithMany(r => r.WeeklyPlans)
                .HasForeignKey(w => w.ReportId)
                .OnDelete(DeleteBehavior.Cascade);

            // WeeklyPlan -> WeeklyPlanDetail (1-to-many)
            builder.Entity<WeeklyPlanDetail>()
                .HasOne(d => d.WeeklyPlan)
                .WithMany(w => w.Details)
                .HasForeignKey(d => d.WeeklyPlanId)
                .OnDelete(DeleteBehavior.Cascade);

            // WeeklyPlanDetail -> OrderTrackingMaster (many-to-1, optional)
            builder.Entity<WeeklyPlanDetail>()
                .HasOne(d => d.MatchedOrder)
                .WithMany(o => o.MatchedInWeeklyPlans)
                .HasForeignKey(d => d.MatchedOrderId)
                .OnDelete(DeleteBehavior.SetNull);
        }
    }
}
