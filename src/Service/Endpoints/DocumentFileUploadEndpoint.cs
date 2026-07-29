using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Services;

namespace Ruoyu.Study.DocLibrary.Service;

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
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentFileUploadEndpoint));

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

        var subject = request.HttpContext.User.FindFirst("sub")?.Value
            ?? request.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var createdBy = Guid.TryParse(subject, out var accountId)
            ? accountId
            : (Guid?)null;

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
}
