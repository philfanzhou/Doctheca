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

internal static class DocumentFileParseEndpoint
{
    internal static RouteHandlerBuilder MapParse(this RouteGroupBuilder group)
    {
        return group.MapPost("/{id:guid}/parse", ParseDocumentFile);
    }

    private static async Task<IResult> ParseDocumentFile(
        Guid id,
        IDocumentFileService fileService,
        IDocumentParseService parseService,
        [FromServices] IOptions<StructaDocOptions> structaDocOptions,
        [FromServices] ILoggerFactory loggerFactory,
        [FromQuery] string modelVersion = "vlm")
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentFileParseEndpoint));

        // Validate modelVersion
        if (modelVersion != "vlm" && modelVersion != "pipeline")
            return Results.BadRequest(new { success = false, message = "modelVersion must be 'vlm' or 'pipeline'", errorCode = "DOCTHECA_INVALID_MODEL_VERSION" });

        var file = await fileService.GetByIdAsync(id);
        if (file == null)
            return Results.NotFound(new { success = false, message = "File not found", errorCode = "DOCTHECA_FILE_NOT_FOUND" });

        // Check in-progress per model version
        var latestParse = await parseService.GetLatestByFileIdAndModelAsync(id, modelVersion);
        if (latestParse != null && (latestParse.Status == DocumentParseStatus.Pending || latestParse.Status == DocumentParseStatus.Parsing))
            return Results.Json(new { success = false, message = $"File already has a {modelVersion} parse in progress", errorCode = "DOCTHECA_PARSE_IN_PROGRESS" }, statusCode: StatusCodes.Status422UnprocessableEntity);

        if (!structaDocOptions.Value.IsConfigured)
            return Results.Json(new { success = false, message = "StructaDoc service is not configured", errorCode = "DOCTHECA_STRUCTADOC_NOT_CONFIGURED" }, statusCode: StatusCodes.Status503ServiceUnavailable);

        var parse = await parseService.CreateAsync(id, modelVersion);
        logger.LogInformation("Document file parse requested: {Id}, ParseId={ParseId}, ModelVersion={ModelVersion}", id, parse.Id, modelVersion);

        return Results.Ok(new
        {
            success = true,
            data = new { id = id.ToString(), parseId = parse.Id.ToString(), status = parse.Status, modelVersion }
        });
    }
}
