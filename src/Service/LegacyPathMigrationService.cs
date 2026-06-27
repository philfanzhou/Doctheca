using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Database;

namespace Ruoyu.Study.DocLibrary.Service;

// MIGRATION NOTICE (2026-06-22):
// This one-time migration moves OSS files from the legacy "docretrieval" prefix to the
// new "doclibrary" prefix, matching the project rename from DocRetrieval to DocLibrary.
// It also updates all database references (file_path / image_path columns) accordingly.
//
// Safe to delete after 2026-07-02 — by then every deployment will have run this migration
// at least once and the legacy prefix will be gone. To remove: delete this file and the
// LegacyPathMigrationService.MigrateAsync(...) call in Program.cs.
public static class LegacyPathMigrationService
{
    private static readonly (string OldPrefix, string NewPrefix)[] PathMappings =
    [
        ("documents/docretrieval/", "documents/doclibrary/"),
        ("documents/docretrieval-files/", "documents/doclibrary-files/")
    ];

    public static async Task MigrateAsync(
        IOssService ossService,
        DocLibraryDbContext dbContext,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var logger = loggerFactory.CreateLogger("LegacyPathMigrationService");

        foreach (var (oldPrefix, newPrefix) in PathMappings)
        {
            await MigratePrefixAsync(ossService, dbContext, oldPrefix, newPrefix, logger, ct);
        }
    }

    private static async Task MigratePrefixAsync(
        IOssService oss, DocLibraryDbContext db,
        string oldPrefix, string newPrefix,
        ILogger logger, CancellationToken ct)
    {
        List<OssObjectInfo> objects;
        try
        {
            objects = await oss.ListObjectsAsync(oldPrefix);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list objects under legacy prefix {Prefix}, skipping migration", oldPrefix);
            return;
        }

        if (objects.Count == 0)
        {
            logger.LogInformation("Legacy prefix {Prefix} is empty or does not exist, skip migration", oldPrefix);
            return;
        }

        logger.LogInformation("Found {Count} objects under legacy prefix {Prefix}, migrating to {NewPrefix}",
            objects.Count, oldPrefix, newPrefix);

        var migrated = 0;
        foreach (var obj in objects)
        {
            var oldPath = obj.ObjectPath;
            var newPath = oldPath.Replace(oldPrefix, newPrefix);

            try
            {
                // Download from old path
                using var stream = await oss.DownloadAsync(oldPath);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                var bytes = ms.ToArray();

                // Upload to new path
                var fileName = Path.GetFileName(newPath);
                var folder = newPrefix.Substring("documents/".Length).TrimEnd('/');
                var contentType = GuessContentType(fileName);
                var uploadedPath = await oss.UploadAsync(bytes, fileName, contentType, OssBucket.Documents, folder);

                // Update database references
                await UpdateDatabasePathsAsync(db, oldPath, uploadedPath, logger, ct);

                // Delete old file
                await oss.DeleteAsync(oldPath);

                logger.LogInformation("Migrated {OldPath} -> {NewPath}", oldPath, uploadedPath);
                migrated++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to migrate {OldPath}, skipping", oldPath);
            }
        }

        logger.LogInformation("Migration completed for {Prefix}: {Migrated}/{Total} files migrated",
            oldPrefix, migrated, objects.Count);
    }

    private static async Task UpdateDatabasePathsAsync(
        DocLibraryDbContext db, string oldPath, string newPath,
        ILogger logger, CancellationToken ct)
    {
        var docs = await db.Documents.Where(d => d.FilePath == oldPath).ToListAsync(ct);
        foreach (var d in docs) d.FilePath = newPath;

        var files = await db.DocumentFiles.Where(f => f.FilePath == oldPath).ToListAsync(ct);
        foreach (var f in files) f.FilePath = newPath;

        var pages = await db.DocumentPages.Where(p => p.ImagePath == oldPath).ToListAsync(ct);
        foreach (var p in pages) p.ImagePath = newPath;

        var images = await db.DocumentParseImages.Where(i => i.ImagePath == oldPath).ToListAsync(ct);
        foreach (var i in images) i.ImagePath = newPath;

        var total = docs.Count + files.Count + pages.Count + images.Count;
        if (total > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Updated {Count} database records for {OldPath} -> {NewPath}", total, oldPath, newPath);
        }
    }

    private static string GuessContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".md" => "text/markdown",
            _ => "application/octet-stream"
        };
    }
}
