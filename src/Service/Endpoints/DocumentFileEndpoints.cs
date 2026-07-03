using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
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

public static class DocumentFileEndpoints
{
    private const long MaxFileSize = 200 * 1024 * 1024; // 200MB

    private static readonly string[] DocumentFileMimeTypes =
    [
        "application/pdf",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    ];

    public static WebApplication MapDocumentFileEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/document-files")
            .RequireAuthorization();

        group.MapPost("/upload", UploadDocumentFile)
            .WithMetadata(new RequestSizeLimitAttribute(200 * 1024 * 1024));
        group.MapGet("/", ListDocumentFiles);
        group.MapGet("/{id:guid}", GetDocumentFile);
        group.MapPost("/{id:guid}/parse", ParseDocumentFile);
        group.MapDelete("/{id:guid}", DeleteDocumentFile);

        return app;
    }

    private static async Task<IResult> UploadDocumentFile(
        HttpRequest request,
        IDocumentFileService fileService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentFileEndpoints));

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
            request.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
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
        var logger = loggerFactory.CreateLogger(nameof(DocumentFileEndpoints));

        var file = await fileService.GetByIdAsync(id);
        if (file == null)
            return Results.NotFound(new { success = false, message = "File not found", errorCode = "DOCLIBRARY_FILE_NOT_FOUND" });

        var allParses = await parseService.GetByFileIdAsync(id);
        var parseResults = new List<object>();

        foreach (var parse in allParses)
        {
            var images = await parseService.GetImagesByParseIdAsync(parse.Id);

            var markdownContent = parse.MarkdownContent;
            if (!string.IsNullOrEmpty(markdownContent))
            {
                markdownContent = await MarkdownExportHelper.ReplaceImagePathsPresignedAsync(
                    markdownContent, images, ossService, logger);
            }

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

            var layoutPdfUrl = await MarkdownExportHelper.GetLayoutPdfPresignedUrlAsync(parse, ossService, logger);

            parseResults.Add(new
            {
                id = parse.Id.ToString(),
                modelVersion = parse.ModelVersion,
                status = parse.Status,
                markdownContent,
                contentList = parse.ContentList,
                layoutPdfUrl,
                errorMessage = parse.ErrorMessage,
                parsedAt = parse.ParsedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                images = imageList,
            });
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
                parses = parseResults,
            }
        });
    }

    private static async Task<IResult> ParseDocumentFile(
        Guid id,
        IDocumentFileService fileService,
        IDocumentParseService parseService,
        [FromServices] IOptions<MinerUOptions> minerUOptions,
        [FromServices] ILoggerFactory loggerFactory,
        [FromQuery] string modelVersion = "vlm")
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentFileEndpoints));

        // Validate modelVersion
        if (modelVersion != "vlm" && modelVersion != "pipeline")
            return Results.BadRequest(new { success = false, message = "modelVersion must be 'vlm' or 'pipeline'", errorCode = "DOCLIBRARY_INVALID_MODEL_VERSION" });

        var file = await fileService.GetByIdAsync(id);
        if (file == null)
            return Results.NotFound(new { success = false, message = "File not found", errorCode = "DOCLIBRARY_FILE_NOT_FOUND" });

        // Check in-progress per model version
        var latestParse = await parseService.GetLatestByFileIdAndModelAsync(id, modelVersion);
        if (latestParse != null && (latestParse.Status == DocumentParseStatus.Pending || latestParse.Status == DocumentParseStatus.Parsing))
            return Results.Json(new { success = false, message = $"File already has a {modelVersion} parse in progress", errorCode = "DOCLIBRARY_PARSE_IN_PROGRESS" }, statusCode: StatusCodes.Status422UnprocessableEntity);

        if (string.IsNullOrEmpty(minerUOptions.Value.ApiToken))
            return Results.Json(new { success = false, message = "MinerU API Token not configured", errorCode = "DOCLIBRARY_MINERU_NOT_CONFIGURED" }, statusCode: StatusCodes.Status503ServiceUnavailable);

        var parse = await parseService.CreateAsync(id, modelVersion);
        logger.LogInformation("Document file parse requested: {Id}, ParseId={ParseId}, ModelVersion={ModelVersion}", id, parse.Id, modelVersion);

        return Results.Ok(new
        {
            success = true,
            data = new { id = id.ToString(), parseId = parse.Id.ToString(), status = parse.Status, modelVersion }
        });
    }

    private static async Task<IResult> DeleteDocumentFile(
        Guid id,
        IDocumentFileService fileService,
        IDocumentParseService parseService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentFileEndpoints));

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
}
