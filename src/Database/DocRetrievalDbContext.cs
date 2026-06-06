using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocRetrieval.Database.Entities;

namespace Ruoyu.Study.DocRetrieval.Database;

public class DocRetrievalDbContext : DbContext
{
    public DocRetrievalDbContext(DbContextOptions<DocRetrievalDbContext> options) : base(options)
    {
    }

    public DbSet<DocumentEntity> Documents { get; set; } = null!;
    public DbSet<DocumentPageEntity> DocumentPages { get; set; } = null!;
    public DbSet<DocumentSegmentEntity> DocumentSegments { get; set; } = null!;
    public DbSet<QuestionSegmentEntity> QuestionSegments { get; set; } = null!;
    public DbSet<DocumentOccurrenceEntity> DocumentOccurrences { get; set; } = null!;
    public DbSet<DocumentIngestionJobEntity> DocumentIngestionJobs { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<DocumentEntity>(entity =>
        {
            entity.HasIndex(e => e.Title).IsUnique();
            entity.HasIndex(e => e.FileHash); // 非唯一索引：允许失败文档的 hash 重复，业务层按 file_hash+status='ready' 校验唯一性
            entity.HasIndex(e => new { e.Subject, e.Grade, e.Year });
            entity.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<DocumentPageEntity>(entity =>
        {
            entity.HasIndex(e => new { e.DocumentId, e.PageNumber }).IsUnique();

            entity.HasOne(e => e.Document)
                .WithMany()
                .HasForeignKey(e => e.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentSegmentEntity>(entity =>
        {
            entity.HasIndex(e => new { e.DocumentId, e.SentenceId }).IsUnique();

            entity.HasOne(e => e.Document)
                .WithMany()
                .HasForeignKey(e => e.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Page)
                .WithMany()
                .HasForeignKey(e => e.PageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QuestionSegmentEntity>(entity =>
        {
            entity.HasIndex(e => new { e.DocumentId, e.QuestionId }).IsUnique();

            entity.HasOne(e => e.Document)
                .WithMany()
                .HasForeignKey(e => e.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Page)
                .WithMany()
                .HasForeignKey(e => e.PageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentOccurrenceEntity>(entity =>
        {
            entity.HasIndex(e => new { e.DocumentId, e.TokenText });
            entity.HasIndex(e => new { e.DocumentId, e.TokenStem });

            entity.HasOne(e => e.Document)
                .WithMany()
                .HasForeignKey(e => e.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Segment)
                .WithMany()
                .HasForeignKey(e => e.SegmentId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.QuestionSegment)
                .WithMany()
                .HasForeignKey(e => e.QuestionSegmentId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DocumentIngestionJobEntity>(entity =>
        {
            entity.HasIndex(e => e.Status);

            entity.HasOne(e => e.Document)
                .WithMany()
                .HasForeignKey(e => e.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
