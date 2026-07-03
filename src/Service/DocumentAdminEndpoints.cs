using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Services;

namespace Ruoyu.Study.DocLibrary.Service;

public static class DocumentAdminEndpoints
{
    private const long MaxFileSize = 200 * 1024 * 1024; // 200MB

    public static WebApplication MapDocumentAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/documents")
            .RequireAuthorization();

        group.MapGet("/search", Search);

        // Document Files endpoints (persistent MinerU flow)
        var fileGroup = app.MapGroup("/admin/document-files")
            .RequireAuthorization();

        fileGroup.MapPost("/upload", UploadDocumentFile)
            .WithMetadata(new RequestSizeLimitAttribute(200 * 1024 * 1024));
        fileGroup.MapGet("/", ListDocumentFiles);
        fileGroup.MapGet("/{id:guid}", GetDocumentFile);
        fileGroup.MapPost("/{id:guid}/parse", ParseDocumentFile);
        fileGroup.MapDelete("/{id:guid}", DeleteDocumentFile);
        fileGroup.MapGet("/{id:guid}/export/markdown", ExportMarkdown);
        fileGroup.MapGet("/{id:guid}/export/html", ExportHtml);

        // Document Parses endpoints
        var parseGroup = app.MapGroup("/admin/document-parses")
            .RequireAuthorization();

        parseGroup.MapGet("/", ListDocumentParses);
        parseGroup.MapDelete("/{parseId:guid}", DeleteDocumentParse);
        parseGroup.MapGet("/{parseId:guid}/export/markdown", ExportParseMarkdown);
        parseGroup.MapGet("/{parseId:guid}/export/html", ExportParseHtml);

        return app;
    }

    private static async Task<IResult> Search(
        ISearchDomainService searchService,
        [FromServices] ILoggerFactory loggerFactory,
        [FromQuery] string query,
        [FromQuery] bool phrase = false,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? pageToken = null,
        [FromQuery] string? subject = null,
        [FromQuery] string? grade = null,
        [FromQuery] string? year = null,
        [FromQuery] string? documentTitle = null)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");

        if (string.IsNullOrWhiteSpace(query))
            return Results.BadRequest(new { success = false, message = "Query cannot be empty", errorCode = "DOCLIBRARY_QUERY_REQUIRED" });

        if (query.Length > 200)
            return Results.BadRequest(new { success = false, message = "Query exceeds 200 characters", errorCode = "DOCLIBRARY_QUERY_TOO_LONG" });

        pageSize = Math.Min(Math.Max(pageSize, 1), 100);

        SearchFilterModel? filter = null;
        if (!string.IsNullOrWhiteSpace(subject) || !string.IsNullOrWhiteSpace(grade)
            || !string.IsNullOrWhiteSpace(year) || !string.IsNullOrWhiteSpace(documentTitle))
        {
            filter = new SearchFilterModel
            {
                Subject = subject,
                Grade = grade,
                Year = year,
                DocumentTitle = documentTitle
            };
        }

        logger.LogInformation("Search request: query={Query}, phrase={Phrase}, pageSize={PageSize}", query, phrase, pageSize);

        var (results, totalCount, nextToken) = await searchService.ExactSearchAsync(
            query, phrase, filter, pageSize, pageToken);

        logger.LogInformation("Search completed: query={Query}, results={Count}, total={Total}", query, results.Count, totalCount);

        return Results.Ok(new
        {
            results = results.Select(r => new
            {
                documentName = r.DocumentName,
                pageNumber = r.PageNumber,
                associatedText = r.AssociatedText,
                score = r.Score,
                matchType = r.MatchType,
                segmentId = r.SegmentId,
                startOffset = r.StartOffset,
                endOffset = r.EndOffset,
                createdAt = r.CreatedAt?.ToString("o")
            }),
            totalCount,
            nextPageToken = nextToken ?? string.Empty
        });
    }

    // ===== Document Files (Persistent MinerU Flow) =====

    private static readonly string[] DocumentFileMimeTypes =
    [
        "application/pdf",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    ];

    private static async Task<IResult> UploadDocumentFile(
        HttpRequest request,
        IDocumentFileService fileService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");

        if (!request.HasFormContentType)
            return Results.BadRequest(new { success = false, message = "Request must be multipart/form-data" });

        var form = await request.ReadFormAsync();
        var file = form.Files.GetFile("file");
        if (file == null || file.Length == 0)
            return Results.BadRequest(new { success = false, message = "File cannot be empty", errorCode = "DOCLIBRARY_FILE_REQUIRED" });

        if (file.Length > MaxFileSize)
            return Results.BadRequest(new { success = false, message = "File size exceeds 200MB limit" });

        if (!DocumentFileMimeTypes.Contains(file.ContentType))
            return Results.BadRequest(new { success = false, message = "Unsupported file format", errorCode = "DOCLIBRARY_FILE_FORMAT_UNSUPPORTED" });

        string filePath;
        using (var stream = file.OpenReadStream())
        {
            var ext = Path.GetExtension(file.FileName) ?? ".bin";
            var objectName = $"{Guid.NewGuid()}{ext}";
            filePath = await ossService.UploadAsync(stream, objectName, file.ContentType, OssBucket.Documents, "doclibrary-files");
        }

        var createdBy = Guid.TryParse(
            request.HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
            out var uid) ? uid : (Guid?)null;

        var model = new DocumentFileModel
        {
            FileName = file.FileName,
            FilePath = filePath,
            ContentType = file.ContentType,
            CreatedBy = createdBy,
        };

        var created = await fileService.CreateAsync(model);

        logger.LogInformation("Document file uploaded: {Id}, FileName={FileName}", created.Id, created.FileName);

        return Results.Ok(new
        {
            success = true,
            data = new
            {
                id = created.Id.ToString(),
                fileName = created.FileName,
                contentType = created.ContentType,
            }
        });
    }

    private static async Task<IResult> ListDocumentFiles(
        IDocumentFileService fileService,
        IDocumentParseService parseService,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? parseStatus = null,
        [FromQuery] string? fileName = null)
    {
        // Fetch a larger set to allow in-memory filtering by parseStatus
        var fetchPage = 1;
        var fetchSize = string.IsNullOrEmpty(parseStatus) ? pageSize : 200;
        var allEnriched = new List<(DocumentFileModel File, string? ParseStatus, string? ErrorMessage, DateTimeOffset? ParsedAt)>();

        while (true)
        {
            var (items, totalCount) = await fileService.GetListAsync(fetchPage, fetchSize, fileName);
            foreach (var f in items)
            {
                var parse = await parseService.GetLatestByFileIdAsync(f.Id);
                allEnriched.Add((f, parse?.Status, parse?.ErrorMessage, parse?.ParsedAt));
            }
            if (items.Count < fetchSize || allEnriched.Count >= totalCount) break;
            fetchPage++;
        }

        // Filter by parseStatus
        IEnumerable<(DocumentFileModel File, string? ParseStatus, string? ErrorMessage, DateTimeOffset? ParsedAt)> filtered = allEnriched;
        if (!string.IsNullOrEmpty(parseStatus))
        {
            if (parseStatus == "unparsed")
                filtered = allEnriched.Where(x => x.ParseStatus == null);
            else
                filtered = allEnriched.Where(x => x.ParseStatus == parseStatus);
        }

        var totalFiltered = filtered.Count();
        var paged = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var data = paged.Select(f => new
        {
            id = f.File.Id.ToString(),
            fileName = f.File.FileName,
            contentType = f.File.ContentType,
            parseStatus = f.ParseStatus,
            errorMessage = f.ErrorMessage,
            createdBy = f.File.CreatedBy?.ToString(),
            createdAt = f.File.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            parsedAt = f.ParsedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        }).ToList();

        return Results.Ok(new
        {
            success = true,
            data,
            total = totalFiltered,
            page,
            pageSize,
            totalPages = (totalFiltered + pageSize - 1) / pageSize
        });
    }

    private static async Task<IResult> GetDocumentFile(
        Guid id,
        IDocumentFileService fileService,
        IDocumentParseService parseService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");

        var file = await fileService.GetByIdAsync(id);
        if (file == null)
            return Results.NotFound(new { success = false, message = "File not found", errorCode = "DOCLIBRARY_FILE_NOT_FOUND" });

        // Get latest parse record
        var parse = await parseService.GetLatestByFileIdAsync(id);

        // Get associated images
        var images = parse != null
            ? await parseService.GetImagesByParseIdAsync(parse.Id)
            : new List<DocumentParseImageModel>();

        // Replace markdown image paths with presigned URLs
        var markdownContent = parse?.MarkdownContent;
        if (!string.IsNullOrEmpty(markdownContent))
        {
            foreach (var img in images)
            {
                try
                {
                    var presignedUrl = await ossService.GetPresignedUrlAsync(img.ImagePath, 3600);
                    markdownContent = markdownContent.Replace($"({img.ImagePath})", $"({presignedUrl})");
                    // Also try replacing by image name for relative paths
                    markdownContent = markdownContent.Replace($"(images/{img.ImageName})", $"({presignedUrl})");
                    markdownContent = markdownContent.Replace($"src=\"{img.ImagePath}\"", $"src=\"{presignedUrl}\"");
                    markdownContent = markdownContent.Replace($"src=\"images/{img.ImageName}\"", $"src=\"{presignedUrl}\"");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to generate presigned URL for image: {ImagePath}", img.ImagePath);
                }
            }
        }

        // Build image list with presigned URLs
        var imageList = new List<object>();
        foreach (var img in images)
        {
            try
            {
                var presignedUrl = await ossService.GetPresignedUrlAsync(img.ImagePath, 3600);
                imageList.Add(new { id = img.Id.ToString(), imageName = img.ImageName, imageUrl = presignedUrl });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to generate presigned URL for image: {ImagePath}", img.ImagePath);
                imageList.Add(new { id = img.Id.ToString(), imageName = img.ImageName, imageUrl = img.ImagePath });
            }
        }

        return Results.Ok(new
        {
            success = true,
            data = new
            {
                id = file.Id.ToString(),
                fileName = file.FileName,
                contentType = file.ContentType,
                createdAt = file.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                parse = parse != null ? new
                {
                    id = parse.Id.ToString(),
                    status = parse.Status,
                    markdownContent,
                    errorMessage = parse.ErrorMessage,
                    layoutPdfUrl = await GetLayoutPdfPresignedUrlAsync(parse, ossService, logger),
                    parsedAt = parse.ParsedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                    images = imageList,
                } : null,
            }
        });
    }

    private static async Task<IResult> ParseDocumentFile(
        Guid id,
        IDocumentFileService fileService,
        IDocumentParseService parseService,
        [FromServices] IOptions<MinerUOptions> minerUOptions,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");

        var file = await fileService.GetByIdAsync(id);
        if (file == null)
            return Results.NotFound(new { success = false, message = "File not found", errorCode = "DOCLIBRARY_FILE_NOT_FOUND" });

        // Check if there's an active parse (pending or parsing)
        var latestParse = await parseService.GetLatestByFileIdAsync(id);
        if (latestParse != null && (latestParse.Status == DocumentParseStatus.Pending || latestParse.Status == DocumentParseStatus.Parsing))
            return Results.Json(new { success = false, message = "File is not in a parseable state", errorCode = "DOCLIBRARY_PARSE_IN_PROGRESS" }, statusCode: StatusCodes.Status422UnprocessableEntity);

        if (string.IsNullOrEmpty(minerUOptions.Value.ApiToken))
            return Results.Json(new { success = false, message = "MinerU API Token not configured", errorCode = "DOCLIBRARY_MINERU_NOT_CONFIGURED" }, statusCode: StatusCodes.Status503ServiceUnavailable);

        var parse = await parseService.CreateAsync(id);
        logger.LogInformation("Document file parse requested: {Id}, ParseId={ParseId}", id, parse.Id);

        return Results.Ok(new
        {
            success = true,
            data = new { id = id.ToString(), parseId = parse.Id.ToString(), status = parse.Status }
        });
    }

    private static async Task<IResult> DeleteDocumentFile(
        Guid id,
        IDocumentFileService fileService,
        IDocumentParseService parseService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");

        var file = await fileService.GetByIdAsync(id);
        if (file == null)
            return Results.NotFound(new { success = false, message = "File not found", errorCode = "DOCLIBRARY_FILE_NOT_FOUND" });

        // Step 1: Collect all OSS paths to clean up (from DB, including new MinerU artifacts)
        var ossPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ossPaths.Add(file.FilePath);

        var allParses = await parseService.GetByFileIdAsync(id);
        foreach (var parse in allParses)
        {
            if (!string.IsNullOrEmpty(parse.ZipPath)) ossPaths.Add(parse.ZipPath);

            // layout.pdf: not yet stored as a separate column; would go here if added
            // (tracked in zipPath for now since it's a sub-file)

            var images = await parseService.GetImagesByParseIdAsync(parse.Id);
            foreach (var img in images)
            {
                if (!string.IsNullOrEmpty(img.ImagePath)) ossPaths.Add(img.ImagePath);
            }
        }

        // Step 2: Delete database records (cascade: parses → blocks + images, then file)
        await fileService.DeleteAsync(id);

        // Step 3: Best-effort OSS cleanup
        var failedPaths = new List<string>();
        foreach (var path in ossPaths)
        {
            try
            {
                await ossService.DeleteAsync(path);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete OSS path: {Path}", path);
                failedPaths.Add(path);
            }
        }

        if (failedPaths.Count > 0)
        {
            logger.LogWarning("Document file {FileId} deleted but {Count} OSS paths remain: {Paths}",
                id, failedPaths.Count, string.Join(", ", failedPaths));
        }

        return Results.Ok(new
        {
            success = true,
            data = new
            {
                id = id.ToString(),
                deleted = true,
                ossDeleted = ossPaths.Count - failedPaths.Count,
                ossFailed = failedPaths.Count,
            }
        });
    }

    private static async Task<IResult> ExportMarkdown(
        Guid id,
        IDocumentFileService fileService,
        IDocumentParseService parseService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");

        var file = await fileService.GetByIdAsync(id);
        if (file == null)
            return Results.NotFound(new { success = false, message = "File not found", errorCode = "DOCLIBRARY_FILE_NOT_FOUND" });

        var parse = await parseService.GetLatestByFileIdAsync(id);
        if (parse == null || parse.Status != DocumentParseStatus.Parsed)
            return Results.Json(new { success = false, message = "File is not parsed yet", errorCode = "DOCLIBRARY_FILE_NOT_PARSED" }, statusCode: StatusCodes.Status422UnprocessableEntity);

        var images = await parseService.GetImagesByParseIdAsync(parse.Id);
        var markdownContent = parse.MarkdownContent ?? string.Empty;

        // Replace S3 paths in markdown with relative image paths
        foreach (var img in images)
        {
            // Markdown: ![alt](S3Path) → ![alt](images/name)
            markdownContent = markdownContent.Replace($"({img.ImagePath})", $"(images/{img.ImageName})");
            // HTML: <img src="S3Path"> → <img src="images/name">
            markdownContent = markdownContent.Replace($"src=\"{img.ImagePath}\"", $"src=\"images/{img.ImageName}\"");
            // HTML: <img src='S3Path'> → <img src='images/name'>
            markdownContent = markdownContent.Replace($"src='{img.ImagePath}'", $"src='images/{img.ImageName}'");
        }

        // Build ZIP in memory (no 'using' — Results.Stream reads lazily after method returns)
        var ms = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            // Add markdown file
            var mdEntry = archive.CreateEntry(Path.GetFileNameWithoutExtension(file.FileName) + ".md", System.IO.Compression.CompressionLevel.Optimal);
            using (var mdStream = mdEntry.Open())
            using (var writer = new StreamWriter(mdStream))
            {
                await writer.WriteAsync(markdownContent);
            }

            // Add images
            foreach (var img in images)
            {
                try
                {
                    using var imgStream = await ossService.DownloadAsync(img.ImagePath);
                    var imgEntry = archive.CreateEntry($"images/{img.ImageName}", System.IO.Compression.CompressionLevel.Fastest);
                    using var entryStream = imgEntry.Open();
                    await imgStream.CopyToAsync(entryStream);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to download image for export: {ImagePath}", img.ImagePath);
                }
            }
        }

        ms.Position = 0;
        var zipFileName = $"{Path.GetFileNameWithoutExtension(file.FileName)}_markdown.zip";
        return Results.Stream(ms, "application/zip", zipFileName);
    }

    private static async Task<IResult> ExportHtml(
        Guid id,
        IDocumentFileService fileService,
        IDocumentParseService parseService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");

        var file = await fileService.GetByIdAsync(id);
        if (file == null)
            return Results.NotFound(new { success = false, message = "File not found", errorCode = "DOCLIBRARY_FILE_NOT_FOUND" });

        var parse = await parseService.GetLatestByFileIdAsync(id);
        if (parse == null || parse.Status != DocumentParseStatus.Parsed)
            return Results.Json(new { success = false, message = "File is not parsed yet", errorCode = "DOCLIBRARY_FILE_NOT_PARSED" }, statusCode: StatusCodes.Status422UnprocessableEntity);

        var images = await parseService.GetImagesByParseIdAsync(parse.Id);
        var markdownContent = parse.MarkdownContent ?? string.Empty;

        // Replace S3 paths with base64 data URIs
        foreach (var img in images)
        {
            try
            {
                using var imgStream = await ossService.DownloadAsync(img.ImagePath);
                using var imgMs = new MemoryStream();
                await imgStream.CopyToAsync(imgMs);
                var base64 = Convert.ToBase64String(imgMs.ToArray());
                var dataUri = $"data:{img.ContentType};base64,{base64}";

                markdownContent = markdownContent.Replace($"({img.ImagePath})", $"({dataUri})");
                markdownContent = markdownContent.Replace($"src=\"{img.ImagePath}\"", $"src=\"{dataUri}\"");
                markdownContent = markdownContent.Replace($"src='{img.ImagePath}'", $"src='{dataUri}'");
                // Also replace relative paths if any remain
                markdownContent = markdownContent.Replace($"(images/{img.ImageName})", $"({dataUri})");
                markdownContent = markdownContent.Replace($"src=\"images/{img.ImageName}\"", $"src=\"{dataUri}\"");
                markdownContent = markdownContent.Replace($"src='images/{img.ImageName}'", $"src='{dataUri}'");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to download image for HTML export: {ImagePath}", img.ImagePath);
            }
        }

        // Convert markdown to HTML using Markdig
        var htmlBody = Markdig.Markdown.ToHtml(markdownContent);

        // Wrap in full HTML document with inline CSS
        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"zh-CN\">");
        html.AppendLine("<head>");
        html.AppendLine("  <meta charset=\"UTF-8\">");
        html.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        html.AppendLine($"  <title>{System.Net.WebUtility.HtmlEncode(file.FileName)}</title>");
        html.AppendLine("  <style>");
        html.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;");
        html.AppendLine("           max-width: 800px; margin: 0 auto; padding: 20px; line-height: 1.6; color: #333; }");
        html.AppendLine("    img { max-width: 100%; height: auto; }");
        html.AppendLine("    table { border-collapse: collapse; width: 100%; }");
        html.AppendLine("    th, td { border: 1px solid #ddd; padding: 8px; text-align: left; }");
        html.AppendLine("    blockquote { border-left: 4px solid #ddd; margin: 0; padding-left: 16px; color: #666; }");
        html.AppendLine("    code { background: #f4f4f4; padding: 2px 6px; border-radius: 3px; }");
        html.AppendLine("    pre { background: #f4f4f4; padding: 16px; overflow-x: auto; border-radius: 6px; }");
        html.AppendLine("  </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine(htmlBody);
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        var htmlFileName = $"{Path.GetFileNameWithoutExtension(file.FileName)}.html";
        var htmlBytes = System.Text.Encoding.UTF8.GetBytes(html.ToString());
        var htmlStream = new MemoryStream(htmlBytes);
        return Results.Stream(htmlStream, "text/html", htmlFileName);
    }

    // ===== Document Parses Endpoints =====

    private static async Task<IResult> ListDocumentParses(
        IDocumentParseService parseService,
        IDocumentFileService fileService,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null)
    {
        var (items, totalCount) = await parseService.GetListAsync(page, pageSize, search);

        var data = new List<object>();
        foreach (var p in items)
        {
            var file = await fileService.GetByIdAsync(p.DocumentFileId);
            data.Add(new
            {
                id = p.Id.ToString(),
                fileName = file?.FileName ?? "Unknown",
                status = p.Status,
                parsedAt = p.ParsedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                errorMessage = p.ErrorMessage,
            });
        }

        return Results.Ok(new
        {
            success = true,
            data,
            total = totalCount,
            page,
            pageSize,
            totalPages = (totalCount + pageSize - 1) / pageSize
        });
    }

    private static async Task<IResult> DeleteDocumentParse(
        Guid parseId,
        IDocumentParseService parseService,
        IDocumentFileService fileService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");

        var parse = await parseService.GetByIdAsync(parseId);
        if (parse == null)
            return Results.NotFound(new { success = false, message = "Parse record not found", errorCode = "DOCLIBRARY_PARSE_NOT_FOUND" });

        // Delete associated images from S3
        var images = await parseService.GetImagesByParseIdAsync(parseId);
        foreach (var img in images)
        {
            try
            {
                await ossService.DeleteAsync(img.ImagePath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete image from OSS: {ImagePath}", img.ImagePath);
            }
        }

        // Delete parse record (cascade deletes images from DB)
        await parseService.DeleteParseAsync(parseId);

        return Results.Ok(new
        {
            success = true,
            data = new { id = parseId.ToString(), deleted = true }
        });
    }

    private static async Task<IResult> ExportParseMarkdown(
        Guid parseId,
        IDocumentParseService parseService,
        IDocumentFileService fileService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");

        var parse = await parseService.GetByIdAsync(parseId);
        if (parse == null)
            return Results.NotFound(new { success = false, message = "Parse record not found", errorCode = "DOCLIBRARY_PARSE_NOT_FOUND" });

        if (parse.Status != DocumentParseStatus.Parsed)
            return Results.Json(new { success = false, message = "Parse record is not parsed yet", errorCode = "DOCLIBRARY_PARSE_NOT_PARSED" }, statusCode: StatusCodes.Status422UnprocessableEntity);

        var file = await fileService.GetByIdAsync(parse.DocumentFileId);
        var fileName = file?.FileName ?? "document";

        var images = await parseService.GetImagesByParseIdAsync(parseId);
        var markdownContent = parse.MarkdownContent ?? string.Empty;

        // Replace S3 paths in markdown with relative image paths
        foreach (var img in images)
        {
            // Markdown: ![alt](S3Path) → ![alt](images/name)
            markdownContent = markdownContent.Replace($"({img.ImagePath})", $"(images/{img.ImageName})");
            // HTML: <img src="S3Path"> → <img src="images/name">
            markdownContent = markdownContent.Replace($"src=\"{img.ImagePath}\"", $"src=\"images/{img.ImageName}\"");
            // HTML: <img src='S3Path'> → <img src='images/name'>
            markdownContent = markdownContent.Replace($"src='{img.ImagePath}'", $"src='images/{img.ImageName}'");
        }

        // Build ZIP in memory (no 'using' — Results.Stream reads lazily after method returns)
        var ms = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            var mdEntry = archive.CreateEntry(Path.GetFileNameWithoutExtension(fileName) + ".md", System.IO.Compression.CompressionLevel.Optimal);
            using (var mdStream = mdEntry.Open())
            using (var writer = new StreamWriter(mdStream))
            {
                await writer.WriteAsync(markdownContent);
            }

            foreach (var img in images)
            {
                try
                {
                    using var imgStream = await ossService.DownloadAsync(img.ImagePath);
                    var imgEntry = archive.CreateEntry($"images/{img.ImageName}", System.IO.Compression.CompressionLevel.Fastest);
                    using var entryStream = imgEntry.Open();
                    await imgStream.CopyToAsync(entryStream);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to download image for export: {ImagePath}", img.ImagePath);
                }
            }
        }

        ms.Position = 0;
        var zipFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_markdown.zip";
        return Results.Stream(ms, "application/zip", zipFileName);
    }

    private static async Task<IResult> ExportParseHtml(
        Guid parseId,
        IDocumentParseService parseService,
        IDocumentFileService fileService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");

        var parse = await parseService.GetByIdAsync(parseId);
        if (parse == null)
            return Results.NotFound(new { success = false, message = "Parse record not found", errorCode = "DOCLIBRARY_PARSE_NOT_FOUND" });

        if (parse.Status != DocumentParseStatus.Parsed)
            return Results.Json(new { success = false, message = "Parse record is not parsed yet", errorCode = "DOCLIBRARY_PARSE_NOT_PARSED" }, statusCode: StatusCodes.Status422UnprocessableEntity);

        var file = await fileService.GetByIdAsync(parse.DocumentFileId);
        var fileName = file?.FileName ?? "document";

        var images = await parseService.GetImagesByParseIdAsync(parseId);
        var markdownContent = parse.MarkdownContent ?? string.Empty;

        // Replace S3 paths with base64 data URIs
        foreach (var img in images)
        {
            try
            {
                using var imgStream = await ossService.DownloadAsync(img.ImagePath);
                using var imgMs = new MemoryStream();
                await imgStream.CopyToAsync(imgMs);
                var base64 = Convert.ToBase64String(imgMs.ToArray());
                var dataUri = $"data:{img.ContentType};base64,{base64}";

                markdownContent = markdownContent.Replace($"({img.ImagePath})", $"({dataUri})");
                markdownContent = markdownContent.Replace($"src=\"{img.ImagePath}\"", $"src=\"{dataUri}\"");
                markdownContent = markdownContent.Replace($"(images/{img.ImageName})", $"({dataUri})");
                markdownContent = markdownContent.Replace($"src=\"images/{img.ImageName}\"", $"src=\"{dataUri}\"");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to download image for HTML export: {ImagePath}", img.ImagePath);
            }
        }

        // Convert markdown to HTML using Markdig
        var htmlBody = Markdig.Markdown.ToHtml(markdownContent);

        // Wrap in full HTML document with inline CSS
        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"zh-CN\">");
        html.AppendLine("<head>");
        html.AppendLine("  <meta charset=\"UTF-8\">");
        html.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        html.AppendLine($"  <title>{System.Net.WebUtility.HtmlEncode(fileName)}</title>");
        html.AppendLine("  <style>");
        html.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;");
        html.AppendLine("           max-width: 800px; margin: 0 auto; padding: 20px; line-height: 1.6; color: #333; }");
        html.AppendLine("    img { max-width: 100%; height: auto; }");
        html.AppendLine("    table { border-collapse: collapse; width: 100%; }");
        html.AppendLine("    th, td { border: 1px solid #ddd; padding: 8px; text-align: left; }");
        html.AppendLine("    blockquote { border-left: 4px solid #ddd; margin: 0; padding-left: 16px; color: #666; }");
        html.AppendLine("    code { background: #f4f4f4; padding: 2px 6px; border-radius: 3px; }");
        html.AppendLine("    pre { background: #f4f4f4; padding: 16px; overflow-x: auto; border-radius: 6px; }");
        html.AppendLine("  </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine(htmlBody);
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        var htmlFileName = $"{Path.GetFileNameWithoutExtension(fileName)}.html";
        var htmlBytes = System.Text.Encoding.UTF8.GetBytes(html.ToString());
        var htmlStream = new MemoryStream(htmlBytes);
        return Results.Stream(htmlStream, "text/html", htmlFileName);
    }

    private static async Task<string?> GetLayoutPdfPresignedUrlAsync(
        DocumentParseModel parse,
        IOssService ossService,
        ILogger logger)
    {
        if (parse.Status != DocumentParseStatus.Parsed || string.IsNullOrEmpty(parse.LayoutPdfPath))
            return null;

        try
        {
            return await ossService.GetPresignedUrlAsync(parse.LayoutPdfPath, 3600);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to generate presigned URL for layout PDF: {Path}", parse.LayoutPdfPath);
            return null;
        }
    }
}
