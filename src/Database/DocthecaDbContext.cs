using Microsoft.EntityFrameworkCore;
using Doctheca.Database.Entities;

namespace Doctheca.Database;

public class DocthecaDbContext : DbContext
{
    public DocthecaDbContext(DbContextOptions<DocthecaDbContext> options) : base(options)
    {
    }

    public DbSet<DocumentFileEntity> DocumentFiles { get; set; } = null!;
    public DbSet<DocumentParseEntity> DocumentParses { get; set; } = null!;
    public DbSet<DocumentParseImageEntity> DocumentParseImages { get; set; } = null!;
    public DbSet<DocumentParseBlockEntity> DocumentParseBlocks { get; set; } = null!;
    public DbSet<StructureDefinitionEntity> StructureDefinitions { get; set; } = null!;
    public DbSet<StructureLevelEntity> StructureLevels { get; set; } = null!;
    public DbSet<DocumentStructureNodeEntity> DocumentStructureNodes { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<DocumentFileEntity>(entity =>
        {
            entity.HasIndex(e => e.FileName);

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("NOW()");

            // Trigger to auto-update updated_at on row modification
            entity.ToTable(tb => tb.HasTrigger("set_document_files_updated_at"));

            entity.HasIndex(e => e.StructureDefinitionId);
            // Deleting a definition that no marked node uses clears the documents' choice.
            entity.HasOne<StructureDefinitionEntity>()
                .WithMany()
                .HasForeignKey(e => e.StructureDefinitionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DocumentParseEntity>(entity =>
        {
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.DocumentFileId);
            entity.HasIndex(e => e.ModelVersion);

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
            // Global reading order; NULLs (not yet numbered) never collide.
            entity.HasIndex(e => new { e.ParseId, e.Sequence }).IsUnique();

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

        modelBuilder.Entity<StructureDefinitionEntity>(entity =>
        {
            entity.HasMany(e => e.Levels)
                .WithOne(e => e.Definition)
                .HasForeignKey(e => e.DefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StructureLevelEntity>(entity =>
        {
            entity.HasIndex(e => e.DefinitionId);
            entity.HasIndex(e => e.ParentLevelId);

            // A level with child levels is removed only after its children (service rule).
            entity.HasOne(e => e.ParentLevel)
                .WithMany()
                .HasForeignKey(e => e.ParentLevelId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DocumentStructureNodeEntity>(entity =>
        {
            entity.HasIndex(e => e.ParseId);
            entity.HasIndex(e => e.LevelId);
            entity.HasIndex(e => e.ParentId);

            entity.HasOne(e => e.Parse)
                .WithMany()
                .HasForeignKey(e => e.ParseId)
                .OnDelete(DeleteBehavior.Cascade);

            // Levels used by marked nodes cannot be deleted (the API answers 409 first).
            entity.HasOne(e => e.Level)
                .WithMany()
                .HasForeignKey(e => e.LevelId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Parent)
                .WithMany()
                .HasForeignKey(e => e.ParentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
