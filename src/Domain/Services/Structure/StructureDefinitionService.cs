using Microsoft.Extensions.Logging;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Repositories;

namespace Doctheca.Domain.Services.Structure;

public interface IStructureDefinitionService
{
    Task<List<StructureDefinitionModel>> ListAsync();
    Task<StructureDefinitionModel?> GetByIdAsync(Guid id);
    Task<StructureResult<StructureDefinitionModel>> CreateAsync(StructureDefinitionInput input);
    Task<StructureResult<StructureDefinitionModel>> UpdateAsync(Guid id, StructureDefinitionInput input);
    Task<StructureResult<bool>> DeleteAsync(Guid id);
}

/// <summary>
/// Creates, edits and deletes runtime structure definitions. Level names are data: nothing here
/// knows any particular level. A level's parent belongs to the same definition, parent cycles are
/// rejected, and levels or definitions used by marked nodes cannot be removed (409).
/// </summary>
public sealed class StructureDefinitionService : IStructureDefinitionService
{
    internal const int MaxDefinitionNameLength = 200;
    internal const int MaxLevelNameLength = 100;
    internal const int MaxLevels = 50;

    private readonly IStructureDefinitionRepository _repository;
    private readonly ILogger<StructureDefinitionService> _logger;
    private readonly TimeProvider _timeProvider;

    public StructureDefinitionService(
        IStructureDefinitionRepository repository,
        ILogger<StructureDefinitionService> logger,
        TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<List<StructureDefinitionModel>> ListAsync() => _repository.ListAsync();

    public Task<StructureDefinitionModel?> GetByIdAsync(Guid id) => _repository.GetByIdAsync(id);

    public async Task<StructureResult<StructureDefinitionModel>> CreateAsync(StructureDefinitionInput input)
    {
        var definitionId = Guid.NewGuid();
        var (levels, errors) = await ValidateAsync(definitionId, input);
        if (errors.Count > 0) return StructureResult<StructureDefinitionModel>.Invalid(errors);

        var now = _timeProvider.GetUtcNow();
        var definition = new StructureDefinitionModel
        {
            Id = definitionId,
            Name = input.Name!.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
            Levels = levels,
        };
        await _repository.AddAsync(definition);
        _logger.LogInformation("Structure definition created: {DefinitionId} with {LevelCount} levels",
            definition.Id, levels.Count);
        return StructureResult<StructureDefinitionModel>.Ok((await _repository.GetByIdAsync(definitionId))!);
    }

    public async Task<StructureResult<StructureDefinitionModel>> UpdateAsync(Guid id, StructureDefinitionInput input)
    {
        var current = await _repository.GetByIdAsync(id);
        if (current == null)
            return StructureResult<StructureDefinitionModel>.NotFound(
                StructureErrorCodes.DefinitionNotFound, "Structure definition not found");

        var (levels, errors) = await ValidateAsync(id, input);
        if (errors.Count > 0) return StructureResult<StructureDefinitionModel>.Invalid(errors);

        // Levels that marked nodes use may be renamed or reordered, but not removed or moved to
        // another parent: either would silently invalidate saved trees.
        var used = await _repository.GetUsedLevelIdsAsync(id);
        foreach (var usedLevel in current.Levels.Where(level => used.Contains(level.Id)))
        {
            var next = levels.FirstOrDefault(level => level.Id == usedLevel.Id);
            if (next == null)
                return StructureResult<StructureDefinitionModel>.Conflict(StructureErrorCodes.LevelInUse,
                    $"Level '{usedLevel.Name}' is used by marked structure nodes and cannot be deleted");
            if (next.ParentLevelId != usedLevel.ParentLevelId)
                return StructureResult<StructureDefinitionModel>.Conflict(StructureErrorCodes.LevelInUse,
                    $"Level '{usedLevel.Name}' is used by marked structure nodes and cannot change its parent level");
        }

        var definition = new StructureDefinitionModel
        {
            Id = id,
            Name = input.Name!.Trim(),
            CreatedAt = current.CreatedAt,
            UpdatedAt = _timeProvider.GetUtcNow(),
            Levels = levels,
        };
        if (!await _repository.UpdateAsync(definition))
            return StructureResult<StructureDefinitionModel>.NotFound(
                StructureErrorCodes.DefinitionNotFound, "Structure definition not found");

        _logger.LogInformation("Structure definition updated: {DefinitionId} with {LevelCount} levels",
            id, levels.Count);
        return StructureResult<StructureDefinitionModel>.Ok((await _repository.GetByIdAsync(id))!);
    }

    public async Task<StructureResult<bool>> DeleteAsync(Guid id)
    {
        var current = await _repository.GetByIdAsync(id);
        if (current == null)
            return StructureResult<bool>.NotFound(StructureErrorCodes.DefinitionNotFound, "Structure definition not found");

        if ((await _repository.GetUsedLevelIdsAsync(id)).Count > 0)
            return StructureResult<bool>.Conflict(StructureErrorCodes.DefinitionInUse,
                "Structure definition is used by marked structure nodes and cannot be deleted");

        await _repository.DeleteAsync(id);
        _logger.LogInformation("Structure definition deleted: {DefinitionId}", id);
        return StructureResult<bool>.Ok(true);
    }

    private async Task<(List<StructureLevelModel> Levels, List<StructureFieldError> Errors)> ValidateAsync(
        Guid definitionId, StructureDefinitionInput input)
    {
        var errors = new List<StructureFieldError>();
        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            errors.Add(new("name", "is required"));
        else if (name.Length > MaxDefinitionNameLength)
            errors.Add(new("name", $"must be at most {MaxDefinitionNameLength} characters"));

        var inputs = input.Levels ?? [];
        if (inputs.Count == 0)
        {
            errors.Add(new("levels", "at least one level is required"));
            return ([], errors);
        }
        if (inputs.Count > MaxLevels)
        {
            errors.Add(new("levels", $"at most {MaxLevels} levels are allowed"));
            return ([], errors);
        }

        var levels = new List<StructureLevelModel>(inputs.Count);
        var seen = new HashSet<Guid>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var level = inputs[i];
            var path = $"levels[{i}]";
            var levelName = level.Name?.Trim();
            if (string.IsNullOrEmpty(levelName))
                errors.Add(new($"{path}.name", "is required"));
            else if (levelName.Length > MaxLevelNameLength)
                errors.Add(new($"{path}.name", $"must be at most {MaxLevelNameLength} characters"));

            var id = level.Id ?? Guid.NewGuid();
            if (level.Id is { } given)
            {
                if (!seen.Add(given))
                    errors.Add(new($"{path}.id", "is used by more than one level"));
                var owner = await _repository.GetDefinitionIdOfLevelAsync(given);
                if (owner is { } ownerId && ownerId != definitionId)
                    errors.Add(new($"{path}.id", "belongs to another structure definition"));
            }

            levels.Add(new StructureLevelModel
            {
                Id = id,
                DefinitionId = definitionId,
                Name = levelName ?? string.Empty,
                ParentLevelId = level.ParentLevelId,
                SortOrder = i,
                Required = level.Required,
            });
        }

        var ids = levels.Select(level => level.Id).ToHashSet();
        for (var i = 0; i < levels.Count; i++)
        {
            if (levels[i].ParentLevelId is not { } parent) continue;
            if (parent == levels[i].Id)
                errors.Add(new($"levels[{i}].parentLevelId", "a level cannot be its own parent"));
            else if (!ids.Contains(parent))
                errors.Add(new($"levels[{i}].parentLevelId", "must reference a level of the same structure definition"));
        }

        if (errors.Count == 0)
        {
            var byId = levels.ToDictionary(level => level.Id);
            for (var i = 0; i < levels.Count; i++)
            {
                var visited = new HashSet<Guid> { levels[i].Id };
                for (var parent = levels[i].ParentLevelId; parent is { } p; parent = byId[p].ParentLevelId)
                {
                    if (!visited.Add(p))
                    {
                        errors.Add(new($"levels[{i}].parentLevelId", "creates a parent cycle"));
                        break;
                    }
                }
            }

            if (levels.All(level => level.ParentLevelId != null))
                errors.Add(new("levels", "at least one top level (without a parent) is required"));
        }

        // Required policy is checked only after the complete graph is structurally valid.
        if (errors.Count == 0)
        {
            var byId = levels.ToDictionary(level => level.Id);
            for (var i = 0; i < levels.Count; i++)
            {
                if (!levels[i].Required) continue;
                for (var parent = levels[i].ParentLevelId; parent is { } p; parent = byId[p].ParentLevelId)
                {
                    if (!byId[p].Required)
                    {
                        errors.Add(new($"levels[{i}].required", "all ancestors of a required level must also be required"));
                        break;
                    }
                }
            }
        }

        return (levels, errors);
    }
}
