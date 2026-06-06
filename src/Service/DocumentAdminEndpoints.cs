using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocRetrieval.Domain.Models;
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
        app.MapPost("/admin/documents/upload", UploadDocument);
        app.MapGet("/admin/documents", ListDocuments);
        app.MapGet("/admin/documents/{id}/status", GetDocumentStatus);
        app.MapDelete("/admin/documents/{title}", DeleteDocument);
        app.MapPut("/admin/documents/{title}/metadata", UpdateMetadata);

        return app;
    }

    private static async Task<IResult> UploadDocument(
        HttpRequest request,
        DocumentDomainService documentService,
        IOssService ossService,
        ILogger logger)
    {
        if (!request.HasFormContentType)
            return Results.BadRequest(new { success = false, message = "请求必须是 multipart/form-data" });

        var form = await request.ReadFormAsync();

        var file = form.Files.GetFile("file");
        if (file == null || file.Length == 0)
            return Results.BadRequest(new { success = false, message = "文件不能为空", errorCode = "DOCRETRIEVAL_QUERY_REQUIRED" });

        if (file.Length > MaxFileSize)
            return Results.BadRequest(new { success = false, message = "文件大小超过200MB限制" });

        if (!AllowedMimeTypes.Contains(file.ContentType))
            return Results.BadRequest(new { success = false, message = "不支持的文件格式", errorCode = "DOCRETRIEVAL_FILE_FORMAT_UNSUPPORTED" });

        var title = form["title"].ToString();
        var subject = form["subject"].ToString();
        var grade = form["grade"].ToString();
        var year = form["year"].ToString();
        var tags = form["tags"].ToString();

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(subject)
            || string.IsNullOrWhiteSpace(grade) || string.IsNullOrWhiteSpace(year))
            return Results.BadRequest(new { success = false, message = "必填元数据缺失（title/subject/grade/year）", errorCode = "DOCRETRIEVAL_METADATA_REQUIRED" });

        string fileHash;
        string filePath;
        using (var stream = file.OpenReadStream())
        {
            using var sha256 = SHA256.Create();
            fileHash = BitConverter.ToString(await sha256.ComputeHashAsync(stream)).Replace("-", "").ToLowerInvariant();
            stream.Position = 0;

            var ext = Path.GetExtension(file.FileName) ?? ".bin";
            var objectName = $"docretrieval/{Guid.NewGuid()}{ext}";
            filePath = await ossService.UploadAsync(stream, objectName, file.ContentType, OssBucket.Uploads);
        }

        var sourceType = file.ContentType switch
        {
            "application/pdf" => "pdf",
            "application/msword" or "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "word",
            "application/vnd.ms-powerpoint" or "application/vnd.openxmlformats-officedocument.presentationml.presentation" => "ppt",
            _ => "unknown"
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
            Tags = string.IsNullOrWhiteSpace(tags) ? null : tags
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
                    document_id = created.Id.ToString(),
                    title = created.Title,
                    job_id = job?.Id.ToString(),
                    status = created.Status
                }
            });
        }
        catch (DocRetrievalValidationException ex)
        {
            var errorCode = ex.Message.Contains("文档名已存在") ? "DOCRETRIEVAL_TITLE_ALREADY_EXISTS"
                : ex.Message.Contains("文件已被导入") ? "DOCRETRIEVAL_FILE_HASH_ALREADY_EXISTS"
                : "DOCRETRIEVAL_METADATA_REQUIRED";
            return Results.Conflict(new { success = false, message = ex.Message, errorCode });
        }
    }

    private static async Task<IResult> ListDocuments(
        DocumentDomainService documentService,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? subject = null,
        [FromQuery] string? grade = null)
    {
        var (items, totalCount) = await documentService.GetDocumentListAsync(page, pageSize, status, subject, grade);

        return Results.Ok(new
        {
            success = true,
            data = items.Select(d => new
            {
                id = d.Id.ToString(),
                title = d.Title,
                source_type = d.SourceType,
                subject = d.Subject,
                grade = d.Grade,
                year = d.Year,
                tags = d.Tags != null ? JsonSerializer.Deserialize<string[]>(d.Tags) : null,
                status = d.Status,
                created_at = d.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                updated_at = d.UpdatedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
            }),
            total = totalCount,
            page,
            pageSize,
            totalPages = (totalCount + pageSize - 1) / pageSize
        });
    }

    private static async Task<IResult> GetDocumentStatus(
        Guid id,
        DocumentDomainService documentService)
    {
        var document = await documentService.GetDocumentAsync(id);
        if (document == null)
            return Results.NotFound(new { success = false, message = "文档不存在", errorCode = "DOCRETRIEVAL_DOCUMENT_NOT_FOUND" });

        var job = await documentService.GetIngestionJobAsync(id);

        return Results.Ok(new
        {
            success = true,
            data = new
            {
                document_id = id.ToString(),
                title = document.Title,
                status = document.Status,
                jobs = job != null ? new[]
                {
                    new
                    {
                        job_id = job.Id.ToString(),
                        status = job.Status,
                        parser_version = job.ParserVersion,
                        ocr_version = job.OcrVersion,
                        error_message = job.ErrorMessage,
                        started_at = job.StartedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                        finished_at = job.FinishedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
                    }
                } : Array.Empty<object>()
            }
        });
    }

    private static async Task<IResult> DeleteDocument(
        string title,
        DocumentDomainService documentService,
        IOssService ossService,
        ILogger logger)
    {
        var document = await documentService.GetDocumentByTitleAsync(title);
        var deleted = await documentService.DeleteDocumentAsync(title);

        if (document != null && !string.IsNullOrEmpty(document.FilePath))
        {
            try
            {
                await ossService.DeleteAsync(document.FilePath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "删除文档文件失败：{FilePath}", document.FilePath);
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
        DocumentDomainService documentService)
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();

        JsonElement json;
        try
        {
            json = JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch
        {
            return Results.BadRequest(new { success = false, message = "无效的JSON" });
        }

        string? subject = json.TryGetProperty("subject", out var s) ? s.GetString() : null;
        string? grade = json.TryGetProperty("grade", out var g) ? g.GetString() : null;
        string? year = json.TryGetProperty("year", out var y) ? y.GetString() : null;
        string? tags = json.TryGetProperty("tags", out var t) ? t.GetRawText() : null;

        if (subject == null && grade == null && year == null && tags == null)
            return Results.BadRequest(new { success = false, message = "至少提供一项元数据" });

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
            var statusCode = ex.Message.Contains("不存在") ? StatusCodes.Status404NotFound
                : ex.Message.Contains("未就绪") ? StatusCodes.Status422UnprocessableEntity
                : StatusCodes.Status400BadRequest;
            return Results.Json(new { success = false, message = ex.Message }, statusCode: statusCode);
        }
    }
}
