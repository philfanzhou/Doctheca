using Microsoft.AspNetCore.Http;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Services.Structure;

namespace Doctheca.Service;

/// <summary>Maps structure results onto the unified JSON envelope (200/400/404/409).</summary>
internal static class StructureHttpResults
{
    internal static IResult From<T>(StructureResult<T> result, Func<T, object> project) => result.Status switch
    {
        StructureResultStatus.Ok => Results.Ok(new { success = true, data = project(result.Value!) }),
        StructureResultStatus.NotFound => Results.NotFound(new { success = false, message = result.Message, errorCode = result.ErrorCode }),
        StructureResultStatus.Conflict => Results.Conflict(new { success = false, message = result.Message, errorCode = result.ErrorCode }),
        _ => Invalid(result.Errors),
    };

    internal static IResult Invalid(IReadOnlyList<StructureFieldError> errors) =>
        Results.BadRequest(new
        {
            success = false,
            message = "Structure validation failed",
            errorCode = StructureErrorCodes.Invalid,
            errors = errors.Select(error => new { field = error.Field, reason = error.Reason }),
        });

    internal static IResult InvalidBody() =>
        Results.BadRequest(new { success = false, message = "Invalid request body", errorCode = StructureErrorCodes.Invalid });

    internal static IResult ParseNotFound() =>
        Results.NotFound(new { success = false, message = "Parse record not found", errorCode = StructureErrorCodes.ParseNotFound });

    internal static object Definition(StructureDefinitionModel definition) => new
    {
        id = definition.Id.ToString(),
        name = definition.Name,
        createdAt = definition.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        updatedAt = definition.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        levels = definition.Levels.Select(level => new
        {
            id = level.Id.ToString(),
            name = level.Name,
            parentLevelId = level.ParentLevelId?.ToString(),
            sortOrder = level.SortOrder,
            required = level.Required,
        }),
    };

    internal static object Tree(DocumentStructureTree tree)
    {
        var levelNames = tree.Definition?.Levels.ToDictionary(level => level.Id, level => level.Name) ?? [];
        var children = tree.Nodes.ToLookup(node => node.ParentId);
        object Node(DocumentStructureNodeModel node) => new
        {
            id = node.Id.ToString(),
            levelId = node.LevelId.ToString(),
            levelName = levelNames.GetValueOrDefault(node.LevelId),
            name = node.Name,
            startSequence = node.StartSequence,
            endSequence = node.EndSequence,
            sortOrder = node.SortOrder,
            children = children[node.Id].OrderBy(child => child.SortOrder).Select(Node).ToList(),
        };

        return new
        {
            parseId = tree.ParseId.ToString(),
            documentFileId = tree.DocumentFileId.ToString(),
            fileName = tree.FileName,
            definition = tree.Definition is null ? null : Definition(tree.Definition),
            nodes = children[null].OrderBy(node => node.SortOrder).Select(Node).ToList(),
        };
    }
}
