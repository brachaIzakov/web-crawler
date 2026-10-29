using Crawler.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Infrastructure.Persistence;

public class CrawlerDbContext : DbContext
{
    public CrawlerDbContext(DbContextOptions<CrawlerDbContext> options) : base(options) { }

    public DbSet<CrawlJob> Jobs => Set<CrawlJob>();
    public DbSet<CrawlPage> Pages => Set<CrawlPage>();
    public DbSet<CrawlEdge> Edges => Set<CrawlEdge>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CrawlJob>(job =>
        {
            job.ToTable("jobs");
            job.HasKey(j => j.Id);
            job.Property(j => j.InputUrl).HasMaxLength(2048).IsRequired();
            job.Property(j => j.StartingDomain).HasMaxLength(253).IsRequired();
            job.Property(j => j.Status).HasConversion<string>().HasMaxLength(16);
            job.Property(j => j.FailureReason).HasMaxLength(1024);
            job.Property(j => j.LeaseOwner).HasMaxLength(128);
            // History is always "most recent first" with Id as tie-breaker: the listing is a plain range scan of this index.
            job.HasIndex(j => new { j.CreatedAt, j.Id }).IsDescending(true, true);
            // Used by the reconciler to find stale Pending/Running jobs.
            job.HasIndex(j => new { j.Status, j.CreatedAt });
        });

        modelBuilder.Entity<CrawlPage>(page =>
        {
            page.ToTable("pages");
            page.HasKey(p => p.Id);
            page.Property(p => p.Url).HasMaxLength(2048).IsRequired();
            page.Property(p => p.FinalUrl).HasMaxLength(2048);
            page.Property(p => p.Title).HasMaxLength(512);
            page.Property(p => p.Error).HasMaxLength(1024);
            page.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            // Postgres text[]; the default keeps the column NOT NULL for rows that predate it.
            page.Property(p => p.OutgoingLinks).HasDefaultValueSql("'{}'");

            page.HasOne(p => p.Job)
                .WithMany(j => j.Pages)
                .HasForeignKey(p => p.JobId)
                .OnDelete(DeleteBehavior.Cascade);

            // NoAction (not Restrict) so deleting a job cascades through pages that reference each other.
            page.HasOne(p => p.ParentPage)
                .WithMany()
                .HasForeignKey(p => p.ParentPageId)
                .OnDelete(DeleteBehavior.NoAction);

            // Idempotency key: a normalized URL exists at most once per job.
            page.HasIndex(p => new { p.JobId, p.Url }).IsUnique();
            // Covers both the worker's frontier query and the tree query (ORDER BY Depth, Id) without a sort.
            page.HasIndex(p => new { p.JobId, p.Depth, p.Id });
        });

        modelBuilder.Entity<CrawlEdge>(edge =>
        {
            edge.ToTable("edges");
            edge.HasKey(e => e.Id);

            edge.HasOne(e => e.Job)
                .WithMany(j => j.Edges)
                .HasForeignKey(e => e.JobId)
                .OnDelete(DeleteBehavior.Cascade);

            edge.HasOne(e => e.ParentPage)
                .WithMany()
                .HasForeignKey(e => e.ParentPageId)
                .OnDelete(DeleteBehavior.Cascade);

            edge.HasOne(e => e.ChildPage)
                .WithMany()
                .HasForeignKey(e => e.ChildPageId)
                .OnDelete(DeleteBehavior.Cascade);

            // Idempotency key: the same parent → child link is stored once per job.
            edge.HasIndex(e => new { e.JobId, e.ParentPageId, e.ChildPageId }).IsUnique();
        });
    }
}
