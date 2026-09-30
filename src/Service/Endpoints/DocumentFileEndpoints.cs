using Microsoft.AspNetCore.Builder;

namespace Doctheca.Service;

public static class DocumentFileEndpoints
{
    public static WebApplication MapDocumentFileEndpoints(this WebApplication app)
    {
        // The whole group answers JSON (upload/list/detail/parse/metadata/delete). The export
        // endpoints live in separately created route groups (DocumentExportEndpoints) and the
        // parse image proxy in the document-parses group, so neither inherits this marker.
        var group = app.MapGroup("/admin/document-files")
            .RequireAuthorization(DocthecaAuthorizationPolicies.Admin)
            .RequireServiceMantleSecurityResponseHeaders();
        group.MapUpload();
        group.MapList();
        group.MapDetail();
        group.MapParse();
        group.MapMetadata();
        group.MapDelete();
        return app;
    }
}
