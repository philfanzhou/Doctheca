using Microsoft.AspNetCore.Builder;

namespace Ruoyu.Study.DocLibrary.Service;

public static class DocumentFileEndpoints
{
    public static WebApplication MapDocumentFileEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/document-files");
        group.MapUpload();
        group.MapList();
        group.MapDetail();
        group.MapParse();
        group.MapMetadata();
        group.MapDelete();
        return app;
    }
}
