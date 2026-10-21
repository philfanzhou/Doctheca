using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Doctheca.Domain.Services.Structure;

namespace Doctheca.Service;

/// <summary>
/// Runtime structure definitions and their levels (issue #89). Levels are configured data; the
/// code knows no level name.
/// </summary>
public static class StructureDefinitionEndpoints
{
    public static WebApplication MapStructureDefinitionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/structure-definitions")
            .RequireAuthorization(DocthecaAuthorizationPolicies.Admin)
            .RequireServiceMantleSecurityResponseHeaders();

        group.MapGet("/", List);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);

        return app;
    }

    /// <summary>Request body of POST and PUT; levels are listed in display order.</summary>
    public sealed record DefinitionRequest(string? Name, List<LevelRequest>? Levels);

    public sealed record LevelRequest(Guid? Id, string? Name, Guid? ParentLevelId);

    private static async Task<IResult> List(IStructureDefinitionService service)
    {
        var definitions = await service.ListAsync();
        return Results.Ok(new { success = true, data = definitions.Select(StructureHttpResults.Definition) });
    }

    private static async Task<IResult> Get(Guid id, IStructureDefinitionService service)
    {
        var definition = await service.GetByIdAsync(id);
        return definition == null
            ? Results.NotFound(new { success = false, message = "Structure definition not found", errorCode = StructureErrorCodes.DefinitionNotFound })
            : Results.Ok(new { success = true, data = StructureHttpResults.Definition(definition) });
    }

    private static async Task<IResult> Create(HttpRequest request, IStructureDefinitionService service, ILoggerFactory loggerFactory)
    {
        var body = await ReadAsync(request, loggerFactory);
        if (body == null) return StructureHttpResults.InvalidBody();

        var result = await service.CreateAsync(ToInput(body));
        if (result.Status == StructureResultStatus.Ok)
        {
            return Results.Created($"/admin/structure-definitions/{result.Value!.Id:D}",
                new { success = true, data = StructureHttpResults.Definition(result.Value) });
        }

        return StructureHttpResults.From(result, StructureHttpResults.Definition);
    }

    private static async Task<IResult> Update(Guid id, HttpRequest request, IStructureDefinitionService service, ILoggerFactory loggerFactory)
    {
        var body = await ReadAsync(request, loggerFactory);
        if (body == null) return StructureHttpResults.InvalidBody();

        return StructureHttpResults.From(await service.UpdateAsync(id, ToInput(body)), StructureHttpResults.Definition);
    }

    private static async Task<IResult> Delete(Guid id, IStructureDefinitionService service)
    {
        return StructureHttpResults.From(await service.DeleteAsync(id),
            _ => new { id = id.ToString(), deleted = true });
    }

    private static StructureDefinitionInput ToInput(DefinitionRequest body) =>
        new(body.Name, body.Levels?.Select(level => level is null
            ? new StructureLevelInput(null, null, null)
            : new StructureLevelInput(level.Id, level.Name, level.ParentLevelId)).ToList());

    private static async Task<DefinitionRequest?> ReadAsync(HttpRequest request, ILoggerFactory loggerFactory)
    {
        try
        {
            return await request.ReadFromJsonAsync<DefinitionRequest>();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            loggerFactory.CreateLogger(nameof(StructureDefinitionEndpoints))
                .LogWarning("Rejected an unreadable structure definition request body");
            return null;
        }
    }
}
