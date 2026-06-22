using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocRetrieval.Database;
using Ruoyu.Study.DocRetrieval.Database.Entities;
using Ruoyu.Study.DocRetrieval.Domain.Exceptions;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;

namespace Ruoyu.Study.DocRetrieval.Service;

public static class DocumentAdminEndpoints
{
    private static readonly string[] AllowedMimeTypes =
    [
        "application/pdf",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    ];

    private const long MaxFileSize = 200 * 1024 * 1024; // 200MB

    public static WebApplication MapDocumentAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/documents")
            .RequireAuthorization();

        group.MapPost("/upload", UploadDocument);
        group.MapGet("/", ListDocuments);
        group.MapGet("/{id:guid}", GetDocument);
        group.MapGet("/{id:guid}/status", GetDocumentStatus);
        group.MapDelete("/{id:guid}", DeleteDocumentById);
        group.MapDelete("/by-title/{title}", DeleteDocument);
        group.MapPut("/{title}/metadata", UpdateMetadata);
        group.MapGet("/search-test", SearchTest);
        group.MapPost("/{id:guid}/retry", RetryIngestion);
        group.MapPost("/{id:guid}/cancel", CancelIngestion);
        group.MapGet("/{id:guid}/segments", GetDocumentSegments);
        group.MapPost("/{id:guid}/refine", RefineDocumentSegments);
        group.MapGet("/scan-consistency", ScanConsistency);
        group.MapDelete("/{id:guid}/force", ForceDeleteDocument);

        // MinerU Agent parsing endpoints
        group.MapPost("/mineru/parse", MinerUParseDocument);
        group.MapGet("/mineru/status/{taskId}", MinerUCheckStatus);
        group.MapGet("/mineru/download/{taskId}", MinerUDownloadResult);

        return app;
    }

    private static async Task<IResult> UploadDocument(
        HttpRequest request,
        IDocumentDomainService documentService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");

        if (!request.HasFormContentType)
            return Results.BadRequest(new { success = false, message = "Request must be multipart/form-data" });

        var form = await request.ReadFormAsync();

        var file = form.Files.GetFile("file");
        if (file == null || file.Length == 0)
            return Results.BadRequest(new { success = false, message = "File cannot be empty", errorCode = "DOCRETRIEVAL_FILE_REQUIRED" });

        if (file.Length > MaxFileSize)
            return Results.BadRequest(new { success = false, message = "File size exceeds 200MB limit" });

        if (!AllowedMimeTypes.Contains(file.ContentType))
            return Results.BadRequest(new { success = false, message = "Unsupported file format", errorCode = "DOCRETRIEVAL_FILE_FORMAT_UNSUPPORTED" });

        var title = form["title"].ToString();
        var subject = form["subject"].ToString();
        var grade = form["grade"].ToString();
        var year = form["year"].ToString();
        var tags = form["tags"].ToString();

        // Auto-fill title from filename if not provided
        if (string.IsNullOrWhiteSpace(title))
        {
            title = Path.GetFileNameWithoutExtension(file.FileName);
        }

        if (string.IsNullOrWhiteSpace(title))
            return Results.BadRequest(new { success = false, message = "Title is required", errorCode = "DOCRETRIEVAL_METADATA_REQUIRED" });

        string fileHash;
        string filePath;
        using (var stream = file.OpenReadStream())
        {
            // Encrypted file detection (must reject before async phase)
            if (IsEncryptedPdf(stream, file.ContentType))
                return Results.BadRequest(new { success = false, message = "Encrypted files not supported", errorCode = "DOCRETRIEVAL_FILE_ENCRYPTED" });

            using var sha256 = SHA256.Create();
            fileHash = BitConverter.ToString(await sha256.ComputeHashAsync(stream)).Replace("-", "").ToLowerInvariant();
            stream.Position = 0;

            var ext = Path.GetExtension(file.FileName) ?? ".bin";
            var objectName = $"{Guid.NewGuid()}{ext}";
            filePath = await ossService.UploadAsync(stream, objectName, file.ContentType, OssBucket.Documents, "docretrieval");
        }

        var sourceType = file.ContentType switch
        {
            "application/pdf" => SourceTypes.Pdf,
            "application/msword" or "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => SourceTypes.Word,
            "application/vnd.ms-powerpoint" or "application/vnd.openxmlformats-officedocument.presentationml.presentation" => SourceTypes.Ppt,
            _ => SourceTypes.Unknown
        };

        var document = new DocumentModel
        {
            Title = title,
            SourceType = sourceType,
            FileHash = fileHash,
            FilePath = filePath,
            FileSize = file.Length,
            Language = "en",
            Grade = grade,
            Subject = subject,
            Year = year,
            Tags = string.IsNullOrWhiteSpace(tags) ? null : tags,
            CreatedBy = Guid.TryParse(request.HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var uid) ? uid : null
        };

        try
        {
            var created = await documentService.CreateDocumentAsync(document);
            var job = await documentService.GetIngestionJobAsync(created.Id);

            return Results.Ok(new
            {
                success = true,
                data = new
                {
                    documentId = created.Id.ToString(),
                    title = created.Title,
                    jobId = job?.Id.ToString(),
                    status = created.Status
                }
            });
        }
        catch (DocRetrievalValidationException ex)
        {
            var (statusCode, errorCode) = ex.Message switch
            {
                var msg when msg.Contains("Document title already exists") => (StatusCodes.Status409Conflict, "DOCRETRIEVAL_TITLE_ALREADY_EXISTS"),
                var msg when msg.Contains("File already imported") => (StatusCodes.Status409Conflict, "DOCRETRIEVAL_FILE_HASH_ALREADY_EXISTS"),
                var msg when msg.Contains("Subject only supports") => (StatusCodes.Status400BadRequest, "DOCRETRIEVAL_SUBJECT_INVALID"),
                var msg when msg.Contains("Invalid grade value") => (StatusCodes.Status400BadRequest, "DOCRETRIEVAL_GRADE_INVALID"),
                var msg when msg.Contains("cannot be empty") => (StatusCodes.Status400BadRequest, "DOCRETRIEVAL_METADATA_REQUIRED"),
                _ => (StatusCodes.Status400BadRequest, "DOCRETRIEVAL_METADATA_REQUIRED")
            };
            return Results.Json(new { success = false, message = ex.Message, errorCode }, statusCode: statusCode);
        }
    }

    private static async Task<IResult> ListDocuments(
        IDocumentDomainService documentService,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? subject = null,
        [FromQuery] string? grade = null,
        [FromQuery] string? keyword = null,
        [FromQuery] string? year = null)
    {
        var (items, totalCount) = await documentService.GetDocumentListAsync(page, pageSize, status, subject, grade, keyword, year);

        return Results.Ok(new
        {
            success = true,
            data = items.Select(d => new
            {
                id = d.Id.ToString(),
                title = d.Title,
                sourceType = d.SourceType,
                subject = d.Subject,
                grade = d.Grade,
                year = d.Year,
                tags = d.Tags != null ? JsonSerializer.Deserialize<string[]>(d.Tags) : null,
                status = d.Status,
                createdBy = d.CreatedBy?.ToString(),
                createdAt = d.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                updatedAt = d.UpdatedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
            }),
            total = totalCount,
            page,
            pageSize,
            totalPages = (totalCount + pageSize - 1) / pageSize
        });
    }

    private static async Task<IResult> GetDocumentStatus(
        Guid id,
        IDocumentDomainService documentService)
    {
        var document = await documentService.GetDocumentAsync(id);
        if (document == null)
            return Results.NotFound(new { success = false, message = "Document not found", errorCode = "DOCRETRIEVAL_DOCUMENT_NOT_FOUND" });

        var jobs = await documentService.GetJobsByDocumentIdAsync(id);

        return Results.Ok(new
        {
            success = true,
            data = new
            {
                documentId = id.ToString(),
                title = document.Title,
                status = document.Status,
                jobs = jobs.Select(j => new
                {
                    jobId = j.Id.ToString(),
                    status = j.Status,
                    progress = j.Progress,
                    progressStage = j.ProgressStage,
                    parserVersion = j.ParserVersion,
                    ocrVersion = j.OcrVersion,
                    errorMessage = j.ErrorMessage,
                    startedAt = j.StartedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                    finishedAt = j.FinishedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
                })
            }
        });
    }

    private static async Task<IResult> GetDocument(
        Guid id,
        IDocumentDomainService documentService)
    {
        var document = await documentService.GetDocumentAsync(id);
        if (document == null)
            return Results.NotFound(new { success = false, message = "Document not found", errorCode = "DOCRETRIEVAL_DOCUMENT_NOT_FOUND" });

        return Results.Ok(new
        {
            success = true,
            data = new
            {
                id = document.Id.ToString(),
                title = document.Title,
                sourceType = document.SourceType,
                fileHash = document.FileHash,
                fileSize = document.FileSize,
                language = document.Language,
                subject = document.Subject,
                grade = document.Grade,
                year = document.Year,
                tags = document.Tags != null ? JsonSerializer.Deserialize<string[]>(document.Tags) : null,
                status = document.Status,
                createdAt = document.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                updatedAt = document.UpdatedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
            }
        });
    }

    private static async Task<IResult> DeleteDocumentById(
        Guid id,
        IDocumentDomainService documentService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");

        var document = await documentService.GetDocumentAsync(id);
        if (document == null)
            return Results.NotFound(new { success = false, message = "Document not found", errorCode = "DOCRETRIEVAL_DOCUMENT_NOT_FOUND" });

        var deleted = await documentService.DeleteDocumentAsync(document.Title);

        if (!string.IsNullOrEmpty(document.FilePath))
        {
            try
            {
                await ossService.DeleteAsync(document.FilePath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete document file: {FilePath}", document.FilePath);
            }
        }

        return Results.Ok(new
        {
            success = true,
            data = new { id = id.ToString(), title = document.Title, deleted = true }
        });
    }

    private static async Task<IResult> DeleteDocument(
        string title,
        IDocumentDomainService documentService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");

        // Get document info first (for OSS file deletion), then delete database records, finally delete OSS file
        var document = await documentService.GetDocumentByTitleAsync(title);

        // Delete database records and search index first
        var deleted = await documentService.DeleteDocumentAsync(title);

        // Then delete OSS file (database already deleted, OSS failure does not affect data consistency)
        if (document != null && !string.IsNullOrEmpty(document.FilePath))
        {
            try
            {
                await ossService.DeleteAsync(document.FilePath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete document file: {FilePath}", document.FilePath);
            }
        }

        return Results.Ok(new
        {
            success = true,
            data = new { title, deleted = document != null }
        });
    }

    private static async Task<IResult> UpdateMetadata(
        string title,
        HttpRequest request,
        IDocumentDomainService documentService)
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();
        if (body.Length > 10 * 1024)
            return Results.StatusCode(StatusCodes.Status413RequestEntityTooLarge);

        JsonElement json;
        try
        {
            json = JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch
        {
            return Results.BadRequest(new { success = false, message = "Invalid JSON" });
        }

        string? subject = json.TryGetProperty("subject", out var s) ? s.GetString() : null;
        string? grade = json.TryGetProperty("grade", out var g) ? g.GetString() : null;
        string? year = json.TryGetProperty("year", out var y) ? y.GetString() : null;
        string? tags = json.TryGetProperty("tags", out var t) ? t.GetRawText() : null;

        if (subject == null && grade == null && year == null && tags == null)
            return Results.BadRequest(new { success = false, message = "At least one metadata field required" });

        try
        {
            var updated = await documentService.UpdateMetadataAsync(title, subject, grade, year, tags);
            return Results.Ok(new
            {
                success = true,
                data = new
                {
                    id = updated.Id.ToString(),
                    title = updated.Title,
                    subject = updated.Subject,
                    grade = updated.Grade,
                    year = updated.Year,
                    tags = updated.Tags != null ? JsonSerializer.Deserialize<string[]>(updated.Tags) : null
                }
            });
        }
        catch (DocRetrievalValidationException ex)
        {
            var (statusCode, errorCode) = ex.Message switch
            {
                var msg when msg.Contains("not found") => (StatusCodes.Status404NotFound, "DOCRETRIEVAL_DOCUMENT_NOT_FOUND"),
                var msg when msg.Contains("not ready") => (StatusCodes.Status422UnprocessableEntity, "DOCRETRIEVAL_DOCUMENT_NOT_READY"),
                var msg when msg.Contains("Subject only supports") => (StatusCodes.Status400BadRequest, "DOCRETRIEVAL_SUBJECT_INVALID"),
                var msg when msg.Contains("Invalid grade value") => (StatusCodes.Status400BadRequest, "DOCRETRIEVAL_GRADE_INVALID"),
                _ => (StatusCodes.Status400BadRequest, "DOCRETRIEVAL_METADATA_REQUIRED")
            };
            return Results.Json(new { success = false, message = ex.Message, errorCode }, statusCode: statusCode);
        }
    }

    private static bool IsEncryptedPdf(Stream stream, string contentType)
    {
        if (!contentType.Contains("pdf")) return false;

        var originalPosition = stream.Position;
        try
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            var buffer = new char[4096];
            reader.Read(buffer, 0, buffer.Length);
            var header = new string(buffer);

            // Check for /Encrypt in the PDF header/first chunk
            return header.Contains("/Encrypt");
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static async Task<IResult> SearchTest(
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
            return Results.BadRequest(new { success = false, message = "Query cannot be empty" });

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

    private static async Task<IResult> CancelIngestion(
        Guid id,
        IDocumentDomainService documentService)
    {
        var document = await documentService.GetDocumentAsync(id);
        if (document == null)
            return Results.NotFound(new { success = false, message = "Document not found", errorCode = "DOCRETRIEVAL_DOCUMENT_NOT_FOUND" });

        var job = await documentService.GetIngestionJobAsync(id);
        if (job == null || (job.Status != DocumentStatus.Pending && job.Status != DocumentStatus.Processing))
            return Results.Json(new { success = false, message = "No cancellable job found for this document", errorCode = "DOCRETRIEVAL_JOB_NOT_CANCELLABLE" }, statusCode: StatusCodes.Status422UnprocessableEntity);

        await documentService.CancelIngestionJobAsync(id);
        return Results.Ok(new { success = true, message = "Ingestion job cancelled" });
    }

    private static async Task<IResult> RetryIngestion(
        Guid id,
        IDocumentDomainService documentService)
    {
        try
        {
            await documentService.RetryIngestionAsync(id);
            return Results.Ok(new { success = true, message = "Ingestion retry queued" });
        }
        catch (InvalidOperationException)
        {
            return Results.NotFound(new { success = false, message = "Document not found", errorCode = "DOCRETRIEVAL_DOCUMENT_NOT_FOUND" });
        }
        catch (DocRetrievalValidationException ex)
        {
            return Results.Json(new { success = false, message = ex.Message, errorCode = "DOCRETRIEVAL_DOCUMENT_NOT_FAILED" }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> GetDocumentSegments(
        Guid id,
        IDocumentDomainService documentService)
    {
        try
        {
            var result = await documentService.GetSegmentsAsync(id);
            return Results.Ok(new
            {
                success = true,
                data = new
                {
                    documentId = result.DocumentId.ToString(),
                    title = result.Title,
                    status = result.Status,
                    profile = result.Profile != null ? new
                    {
                        subject = result.Profile.Subject,
                        docType = result.Profile.DocType,
                        segmentStrategy = result.Profile.SegmentStrategy,
                        structure = new
                        {
                            hasChapters = result.Profile.Structure.HasChapters,
                            hasQuestions = result.Profile.Structure.HasQuestions,
                            hasWordList = result.Profile.Structure.HasWordList,
                            hasFormulas = result.Profile.Structure.HasFormulas
                        }
                    } : null,
                    segments = result.Segments.Select(s => new
                    {
                        id = s.Id.ToString(),
                        sentenceId = s.SentenceId,
                        segmentType = s.SegmentType,
                        text = s.Text,
                        startOffset = s.StartOffset,
                        endOffset = s.EndOffset,
                        pageNumber = s.PageNumber
                    }),
                    totalCount = result.TotalCount
                }
            });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { success = false, message = "Document not found", errorCode = "DOCRETRIEVAL_DOCUMENT_NOT_FOUND" });
        }
    }

    private static async Task<IResult> RefineDocumentSegments(
        Guid id,
        HttpRequest request,
        IDocumentDomainService documentService,
        DocRetrievalDbContext dbContext,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");

        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();
        if (body.Length > 100 * 1024)
            return Results.StatusCode(StatusCodes.Status413RequestEntityTooLarge);

        JsonElement json;
        try
        {
            json = JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch
        {
            return Results.BadRequest(new { success = false, message = "Invalid JSON" });
        }

        if (!json.TryGetProperty("corrections", out var correctionsElement) || correctionsElement.GetArrayLength() == 0)
            return Results.BadRequest(new { success = false, message = "Corrections list required" });

        var corrections = new List<SegmentCorrection>();
        foreach (var item in correctionsElement.EnumerateArray())
        {
            var action = item.TryGetProperty("action", out var a) ? a.GetString() : null;
            if (string.IsNullOrWhiteSpace(action) || action is not ("merge" or "split" or "retype" or "splitMerge"))
                return Results.BadRequest(new { success = false, message = $"Invalid action: {action}" });

            var originalIds = new List<string>();
            if (item.TryGetProperty("originalSentenceIds", out var idsElement))
            {
                foreach (var sid in idsElement.EnumerateArray())
                {
                    var sidStr = sid.GetString();
                    if (!string.IsNullOrEmpty(sidStr)) originalIds.Add(sidStr);
                }
            }

            if (originalIds.Count == 0)
                return Results.BadRequest(new { success = false, message = "originalSentenceIds required" });

            corrections.Add(new SegmentCorrection
            {
                OriginalSentenceIds = originalIds,
                Action = action,
                NewText = item.TryGetProperty("newText", out var nt) ? nt.GetString() : null,
                SplitPosition = item.TryGetProperty("splitPosition", out var sp) ? sp.GetInt32() : null,
                NewSegmentType = item.TryGetProperty("newSegmentType", out var nst) ? nst.GetString() : null,
                MergeFirstWithPrevious = item.TryGetProperty("mergeFirstWithPrevious", out var mfp) && mfp.GetBoolean(),
                MergeSecondWithNext = item.TryGetProperty("mergeSecondWithNext", out var msn) && msn.GetBoolean()
            });
        }

        try
        {
            var result = await documentService.RefineSegmentsAsync(id, corrections);

            // Save backup to database (fire-and-forget — failure should not affect refinement result)
            try
            {
                var backupEntity = new DocumentSegmentBackupEntity
                {
                    Id = result.BackupId,
                    DocumentId = id,
                    BackupData = result.BackupDataJson,
                    CorrectionCount = result.CorrectionCount,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                dbContext.DocumentSegmentBackups.Add(backupEntity);
                await dbContext.SaveChangesAsync();
            }
            catch (Exception backupEx)
            {
                logger.LogWarning(backupEx, "Failed to save refinement backup for document {DocumentId}, refinement already committed", id);
            }

            logger.LogInformation("Document refinement completed: {DocumentId}, {CorrectionCount} corrections", id, corrections.Count);

            return Results.Ok(new
            {
                success = true,
                data = new
                {
                    documentId = result.DocumentId.ToString(),
                    backupId = result.BackupId.ToString(),
                    correctionCount = result.CorrectionCount,
                    message = result.Message
                }
            });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { success = false, message = "Document not found", errorCode = "DOCRETRIEVAL_DOCUMENT_NOT_FOUND" });
        }
        catch (DocRetrievalValidationException ex)
        {
            var (statusCode, errorCode) = ex.Message switch
            {
                var msg when msg.Contains("not ready") => (StatusCodes.Status422UnprocessableEntity, "DOCRETRIEVAL_DOCUMENT_NOT_READY"),
                var msg when msg.Contains("Too many") => (StatusCodes.Status400BadRequest, "DOCRETRIEVAL_TOO_MANY_CORRECTIONS"),
                var msg when msg.Contains("not configured") => (StatusCodes.Status503ServiceUnavailable, "DOCRETRIEVAL_LLM_NOT_CONFIGURED"),
                _ => (StatusCodes.Status400BadRequest, "DOCRETRIEVAL_REFINE_FAILED")
            };
            return Results.Json(new { success = false, message = ex.Message, errorCode }, statusCode: statusCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Document refinement failed: {DocumentId}", id);
            return Results.Json(new { success = false, message = "Refinement failed", errorCode = "DOCRETRIEVAL_LLM_REFINE_FAILED" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> ScanConsistency(
        IDocumentRepository documentRepository,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");

        try
        {
            // 1. Get all documents with file_path from DB
            var docs = await documentRepository.GetAllDocumentsWithFilePathAsync();
            var dbFilePaths = docs.Select(d => d.FilePath).ToHashSet();

            // 2. Check OSS existence — dedup by file_path first
            var filePathToDocs = docs.GroupBy(d => d.FilePath)
                .ToDictionary(g => g.Key, g => g.ToList());
            var brokenDocuments = new List<object>();
            foreach (var (filePath, docGroup) in filePathToDocs)
            {
                try
                {
                    var exists = await ossService.ObjectExistsAsync(filePath);
                    if (!exists)
                    {
                        foreach (var (id, title, _, status) in docGroup)
                        {
                            brokenDocuments.Add(new { id = id.ToString(), title, filePath, status });
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to check OSS file: {FilePath}", filePath);
                    foreach (var (id, title, _, status) in docGroup)
                    {
                        brokenDocuments.Add(new { id = id.ToString(), title, filePath, status });
                    }
                }
            }

            // 3. List all OSS files under docretrieval/ prefix
            var orphanOssFiles = new List<string>();
            try
            {
                var ossObjects = await ossService.ListObjectsAsync("documents/docretrieval/");
                foreach (var obj in ossObjects)
                {
                    if (!dbFilePaths.Contains(obj.ObjectPath))
                    {
                        orphanOssFiles.Add(obj.ObjectPath);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to list OSS objects");
            }

            return Results.Ok(new
            {
                success = true,
                data = new
                {
                    orphanOssFiles,
                    brokenDocuments,
                    summary = new
                    {
                        totalDocuments = docs.Count,
                        brokenCount = brokenDocuments.Count,
                        orphanCount = orphanOssFiles.Count
                    }
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Consistency scan failed");
            return Results.Json(new { success = false, message = "Scan failed" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> ForceDeleteDocument(
        Guid id,
        IDocumentDomainService documentService,
        IDocumentRepository documentRepository,
        IOssService ossService,
        ISearchIndexService? searchIndexService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");

        try
        {
            var document = await documentRepository.GetByIdAsync(id);
            if (document == null)
                return Results.NotFound(new { success = false, message = "Document not found", errorCode = "DOCRETRIEVAL_DOCUMENT_NOT_FOUND" });

            // Delete DB records (cascade)
            await documentService.DeleteDocumentAsync(document.Title);

            // Try to delete OSS file (best-effort)
            try
            {
                await ossService.DeleteAsync(document.FilePath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete OSS file during force delete: {FilePath}", document.FilePath);
            }

            // Try to delete search index (best-effort)
            if (searchIndexService != null)
            {
                try
                {
                    await searchIndexService.DeleteDocumentIndexAsync(id);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete search index during force delete: {DocumentId}", id);
                }
            }

            logger.LogInformation("Force deleted document: {DocumentId}, Title={Title}", id, document.Title);
            return Results.Ok(new { success = true, message = "Document force deleted" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Force delete failed: {DocumentId}", id);
            return Results.Json(new { success = false, message = "Force delete failed" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    // ===== MinerU Precision Parsing Endpoints =====

    private const long MinerUMaxFileSize = 200 * 1024 * 1024; // 200MB Precision API limit

    private static async Task<IResult> MinerUParseDocument(
        HttpRequest request,
        MinerUPrecisionClient minerUClient,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");
        try
        {
            var form = await request.ReadFormAsync();

            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { success = false, message = "No file uploaded" });

            if (file.Length > MinerUMaxFileSize)
                return Results.BadRequest(new { success = false, message = $"File size exceeds 200MB limit (current: {file.Length / 1024.0 / 1024.0:F1}MB)" });

            // Step 1: Upload file to our OSS to get a presigned URL
            string presignedUrl;
            using (var stream = file.OpenReadStream())
            {
                var ext = Path.GetExtension(file.FileName) ?? ".bin";
                var objectName = $"mineru-upload/{Guid.NewGuid()}{ext}";
                var contentType = file.ContentType ?? "application/octet-stream";
                var ossPath = await ossService.UploadAsync(stream, objectName, contentType, OssBucket.Documents, "mineru-upload");
                presignedUrl = await ossService.GetPresignedUrlAsync(ossPath, 3600);
            }

            logger.LogInformation("File uploaded to OSS, presigned URL generated for {FileName}", file.FileName);

            // Step 2: Submit to MinerU Precision API with the presigned URL
            var dataId = Guid.NewGuid().ToString("N")[..16];
            var taskId = await minerUClient.SubmitUrlAsync(presignedUrl, dataId);

            logger.LogInformation("MinerU Precision parse submitted: {FileName} -> TaskId={TaskId}", file.FileName, taskId);

            return Results.Ok(new { success = true, data = new { task_id = taskId, file_name = file.FileName } });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "MinerU parse submit failed");
            return Results.Json(new { success = false, message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MinerU parse submit error");
            return Results.Json(new { success = false, message = "Internal error" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> MinerUCheckStatus(
        string taskId,
        MinerUPrecisionClient minerUClient,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");
        try
        {
            var (state, fullZipUrl, errMsg) = await minerUClient.PollStatusAsync(taskId);

            return Results.Ok(new
            {
                success = true,
                data = new
                {
                    task_id = taskId,
                    state,
                    full_zip_url = fullZipUrl,
                    err_msg = errMsg,
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MinerU status check failed for task {TaskId}", taskId);
            return Results.Json(new { success = false, message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> MinerUDownloadResult(
        string taskId,
        MinerUPrecisionClient minerUClient,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocumentAdminEndpoints");
        try
        {
            // First check status to get full_zip_url
            var (state, fullZipUrl, errMsg) = await minerUClient.PollStatusAsync(taskId);

            if (state != "done")
                return Results.BadRequest(new { success = false, message = $"Task not done yet, current state: {state}" });

            if (string.IsNullOrEmpty(fullZipUrl))
                return Results.Json(new { success = false, message = "No full_zip_url in response" }, statusCode: StatusCodes.Status502BadGateway);

            var (markdown, imageCount) = await minerUClient.DownloadAndProcessZipAsync(fullZipUrl, taskId, ossService);

            return Results.Ok(new
            {
                success = true,
                data = new
                {
                    task_id = taskId,
                    markdown,
                    markdown_length = markdown.Length,
                    image_count = imageCount,
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MinerU download failed for task {TaskId}", taskId);
            return Results.Json(new { success = false, message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
