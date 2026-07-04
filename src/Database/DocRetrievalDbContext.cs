using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocLibrary.Database.Entities;

namespace Ruoyu.Study.DocLibrary.Database;

public class DocLibraryDbContext : DbContext
{
    public DocLibraryDbContext(DbContextOptions<DocLibraryDbContext> options) : base(options)
    {
    }

    public DbSet<DocumentEntity> Documents { get; set; } = null!;
    public DbSet<DocumentPageEntity> DocumentPages { get; set; } = null!;
    public DbSet<DocumentSegmentEntity> DocumentSegments { get; set; } = null!;
    public DbSet<QuestionSegmentEntity> QuestionSegments { get; set; } = null!;
    public DbSet<DocumentOccurrenceEntity> DocumentOccurrences { get; set; } = null!;
    public DbSet<DocumentIngestionJobEntity> DocumentIngestionJobs { get; set; } = null!;
    public DbSet<DocumentSegmentBackupEntity> DocumentSegmentBackups { get; set; } = null!;
    public DbSet<DocumentFileEntity> DocumentFiles { get; set; } = null!;
    public DbSet<DocumentParseEntity> DocumentParses { get; set; } = null!;
    public DbSet<DocumentParseImageEntity> DocumentParseImages { get; set; } = null!;
    public DbSet<DocumentParseBlockEntity> DocumentParseBlocks { get; set; } = null!;
    public DbSet<DocumentParseImportEntity> DocumentParseImports { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<DocumentEntity>(entity =>
        {
            entity.HasIndex(e => e.Title).IsUnique();
            entity.HasIndex(e => e.FileHash); // Non-unique index: allows hash duplicates for failed documents, business layer validates uniqueness by file_hash+status='ready'
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

        modelBuilder.Entity<DocumentSegmentBackupEntity>(entity =>
        {
            entity.HasIndex(e => e.DocumentId);

            entity.HasOne(e => e.Document)
                .WithMany()
                .HasForeignKey(e => e.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentFileEntity>(entity =>
        {
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("NOW()");

            // Trigger to auto-update updated_at on row modification
            entity.ToTable(tb => tb.HasTrigger("set_document_files_updated_at"));
        });

        modelBuilder.Entity<DocumentParseEntity>(entity =>
        {
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.DocumentFileId);

            entity.Property(e => e.ContentList).HasColumnType("jsonb");
            entity.Property(e => e.ContentListV2).HasColumnType("jsonb");
            entity.Property(e => e.ModelJson).HasColumnType("jsonb");
            entity.Property(e => e.LayoutJson).HasColumnType("jsonb");

            entity.HasOne(e => e.DocumentFile)
                .WithMany()
                .HasForeignKey(e => e.DocumentFileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentParseImageEntity>(entity =>
        {
            entity.HasIndex(e => e.ParseId);

            entity.HasOne(e => e.Parse)
                .WithMany()
                .HasForeignKey(e => e.ParseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentParseBlockEntity>(entity =>
        {
            entity.HasIndex(e => new { e.ParseId, e.PageId, e.SortIndex });
            entity.HasIndex(e => e.BlockType);
            entity.HasIndex(e => e.ImageId);

            entity.Property(e => e.BlockData).HasColumnType("jsonb");

            entity.HasOne(e => e.Parse)
                .WithMany()
                .HasForeignKey(e => e.ParseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Image)
                .WithMany()
                .HasForeignKey(e => e.ImageId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DocumentParseImportEntity>(entity =>
        {
            entity.HasIndex(e => e.ParseId).IsUnique();
            entity.HasIndex(e => e.Status);

            entity.HasOne(e => e.Parse)
                .WithMany()
                .HasForeignKey(e => e.ParseId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
