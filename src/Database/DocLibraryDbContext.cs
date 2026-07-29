using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocLibrary.Database.Entities;

namespace Ruoyu.Study.DocLibrary.Database;

public class DocLibraryDbContext : DbContext
{
    public DocLibraryDbContext(DbContextOptions<DocLibraryDbContext> options) : base(options)
    {
    }

    public DbSet<DocumentFileEntity> DocumentFiles { get; set; } = null!;
    public DbSet<DocumentParseEntity> DocumentParses { get; set; } = null!;
    public DbSet<DocumentParseImageEntity> DocumentParseImages { get; set; } = null!;
    public DbSet<DocumentParseBlockEntity> DocumentParseBlocks { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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
    }
}
