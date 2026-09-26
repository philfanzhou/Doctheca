using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Doctheca.Domain.Models;
using Doctheca.Domain.Services;
using Doctheca.Service.StructaDoc;

namespace Doctheca.Service;

internal static class DocumentFileUploadEndpoint
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

    internal static RouteHandlerBuilder MapUpload(this RouteGroupBuilder group)
    {
        return group.MapPost("/upload", UploadDocumentFile)
            .WithMetadata(new RequestSizeLimitAttribute(200 * 1024 * 1024));
    }

    private static async Task<IResult> UploadDocumentFile(
        HttpRequest request,
        IDocumentFileService fileService,
        IStructaDocClient structaDocClient,
        IOptions<StructaDocOptions> structaDocOptions,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentFileUploadEndpoint));

        if (!structaDocOptions.Value.IsConfigured)
            return Results.Json(new { success = false, message = "StructaDoc service is not configured", errorCode = "DOCTHECA_STRUCTADOC_NOT_CONFIGURED" }, statusCode: StatusCodes.Status503ServiceUnavailable);

        if (!request.HasFormContentType)
            return Results.BadRequest(new { success = false, message = "Request must be multipart/form-data" });

        var form = await request.ReadFormAsync();
        var file = form.Files.GetFile("file");
        if (file == null || file.Length == 0)
            return Results.BadRequest(new { success = false, message = "File cannot be empty", errorCode = "DOCTHECA_FILE_REQUIRED" });

        if (file.Length > MaxFileSize)
            return Results.BadRequest(new { success = false, message = "File size exceeds 200MB limit" });

        if (!DocumentFileMimeTypes.Contains(file.ContentType))
            return Results.BadRequest(new { success = false, message = "Unsupported file format", errorCode = "DOCTHECA_FILE_FORMAT_UNSUPPORTED" });

        // ADR-0009: StructaDoc owns document originals; Doctheca keeps only the reference.
        StructaDocDocumentResponse uploaded;
        await using (var stream = file.OpenReadStream())
        {
            try
            {
                uploaded = await structaDocClient.UploadDocumentAsync(
                    file.FileName, file.ContentType, stream, request.HttpContext.RequestAborted);
            }
            catch (StructaDocException ex)
            {
                logger.LogError(ex, "StructaDoc document upload failed for {FileName}", file.FileName);
                return MapUploadError(ex);
            }
        }

        var subject = request.HttpContext.User.FindFirst("sub")?.Value
            ?? request.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var createdBy = Guid.TryParse(subject, out var accountId)
            ? accountId
            : (Guid?)null;

        var model = new DocumentFileModel
        {
            FileName = file.FileName,
            FilePath = null,
            StructaDocDocumentId = uploaded.Id,
            ContentType = uploaded.MediaType ?? file.ContentType,
            CreatedBy = createdBy,
        };

        var created = await fileService.CreateAsync(model);

        logger.LogInformation(
            "Document file uploaded to StructaDoc: {Id}, FileName={FileName}, StructaDocDocumentId={StructaDocDocumentId}",
            created.Id, created.FileName, uploaded.Id);

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

    private static IResult MapUploadError(StructaDocException ex)
    {
        if (ex.StatusCode == 413 || ex.ProblemCode == "file-too-large")
            return Results.BadRequest(new { success = false, message = "File size exceeds the StructaDoc upload limit" });

        if (ex.StatusCode == 415 || ex.ProblemCode == "unsupported-document-type")
            return Results.BadRequest(new { success = false, message = "Unsupported file format", errorCode = "DOCTHECA_FILE_FORMAT_UNSUPPORTED" });

        if (ex.StatusCode is 401 or 403)
            return Results.Json(new { success = false, message = "StructaDoc rejected the configured API key", errorCode = "DOCTHECA_STRUCTADOC_UNAUTHORIZED" }, statusCode: StatusCodes.Status502BadGateway);

        return Results.Json(new { success = false, message = ex.Message, errorCode = "DOCTHECA_STRUCTADOC_ERROR" }, statusCode: StatusCodes.Status502BadGateway);
    }
}
