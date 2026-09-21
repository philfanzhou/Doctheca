using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Ruoyu.Study.DocLibrary.Service.StructaDoc;

namespace Ruoyu.Study.DocLibrary.Service;

internal static class DocumentFileDeleteEndpoint
{
    internal static RouteHandlerBuilder MapDelete(this RouteGroupBuilder group)
    {
        return group.MapDelete("/{id:guid}", DeleteDocumentFile);
    }

    private static async Task<IResult> DeleteDocumentFile(
        Guid id,
        IDocumentFileService fileService,
        IDocumentParseService parseService,
        IOssService ossService,
        ISearchIndexService searchIndexService,
        IStructaDocClient structaDocClient,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentFileDeleteEndpoint));

        var file = await fileService.GetByIdAsync(id);
        if (file == null)
            return Results.NotFound(new { success = false, message = "File not found", errorCode = "DOCLIBRARY_FILE_NOT_FOUND" });

        // Step 1: Collect legacy OSS paths to clean up. StructaDoc-backed parses store asset IDs
        // (not OSS paths) and their remote artifacts are owned by StructaDoc (ADR-0009).
        var ossPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(file.FilePath))
        {
            ossPaths.Add(file.FilePath);
        }

        var allParses = await parseService.GetByFileIdAsync(id);
        foreach (var parse in allParses.Where(p => p.StructaDocParseRunId == null))
        {
            if (!string.IsNullOrEmpty(parse.ZipPath)) ossPaths.Add(parse.ZipPath);

            var images = await parseService.GetImagesByParseIdAsync(parse.Id);
            foreach (var img in images)
            {
                if (!string.IsNullOrEmpty(img.ImagePath)) ossPaths.Add(img.ImagePath);
            }
        }

        // Step 2: Delete database records (cascade: parses → blocks + images, then file)
        await fileService.DeleteAsync(id);

        // Step 3: Best-effort OSS cleanup (legacy artifacts only)
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

        // Step 4: Best-effort StructaDoc cleanup: cancel active runs, then delete the document.
        var structaDocDeleted = false;
        if (file.StructaDocDocumentId is Guid documentId)
        {
            try
            {
                foreach (var parse in allParses)
                {
                    if (parse.StructaDocParseRunId is Guid runId
                        && parse.Status is DocumentParseStatus.Pending or DocumentParseStatus.Parsing)
                    {
                        await structaDocClient.CancelParseRunAsync(runId);
                    }
                }

                await structaDocClient.DeleteDocumentAsync(documentId);
                structaDocDeleted = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete StructaDoc document {DocumentId} for file {FileId}", documentId, id);
            }
        }

        // Step 5: Delete OpenSearch index for this document file (best-effort, does not block deletion)
        try
        {
            await searchIndexService.DeleteDocumentFileIndexAsync(id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete OpenSearch index for document file {FileId}", id);
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
                structaDocDeleted,
            }
        });
    }
}
