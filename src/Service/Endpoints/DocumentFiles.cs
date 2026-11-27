using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Doctheca.Service.Endpoints;

/// <summary>
/// The administrator's document library under <c>/admin/document-files</c>: authoring (create,
/// first source, tree), upload, listing, detail, parsing, metadata and deletion. All JSON, all
/// behind <c>DocthecaAdmin</c>.
/// </summary>
public static partial class DocumentFiles
{
    internal const long MaxFileSize = 200 * 1024 * 1024; // 200MB

    /// <summary>The document formats StructaDoc accepts for a source upload.</summary>
    internal static readonly string[] MimeTypes =
    [
        "application/pdf",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    ];

    public static WebApplication MapDocumentFiles(this WebApplication app)
    {
        // The whole group answers JSON. The export endpoints live in separately created route
        // groups (DocumentExport) and the parse image proxy in the document-parses group, so
        // neither inherits this marker.
        var group = app.MapGroup("/admin/document-files")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .RequireServiceMantleSecurityResponseHeaders();

        group.MapPost("/", Create);
        group.MapPost("/{id:guid}/source", Attach).WithMetadata(new RequestSizeLimitAttribute(MaxFileSize));
        group.MapGet("/{id:guid}/structure", GetTree);
        group.MapPut("/{id:guid}/structure", SaveTree);
        group.MapUpload();
        group.MapGet("/", List);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/{id:guid}/parse", Parse);
        group.MapPut("/{id:guid}/metadata", UpdateMetadata);
        group.MapDelete("/{id:guid}", Delete);
        return app;
    }

    /// <summary>Maps only the source upload, so the authorization tests can exercise that route alone.</summary>
    internal static RouteHandlerBuilder MapUpload(this RouteGroupBuilder group) =>
        group.MapPost("/upload", Upload).WithMetadata(new RequestSizeLimitAttribute(MaxFileSize));
}
