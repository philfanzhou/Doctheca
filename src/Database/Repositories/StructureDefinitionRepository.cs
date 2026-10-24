using Microsoft.EntityFrameworkCore;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Repositories;

namespace Doctheca.Database.Repositories;

public class StructureDefinitionRepository : IStructureDefinitionRepository
{
    private readonly DocthecaDbContext _context;

    public StructureDefinitionRepository(DocthecaDbContext context)
    {
        _context = context;
    }

    public async Task<List<StructureDefinitionModel>> ListAsync()
    {
        var entities = await _context.StructureDefinitions
            .AsNoTracking()
            .Include(e => e.Levels)
            .OrderBy(e => e.Name)
            .ThenBy(e => e.CreatedAt)
            .ToListAsync();
        return entities.Select(MapToModel).ToList();
    }

    public async Task<StructureDefinitionModel?> GetByIdAsync(Guid id)
    {
        var entity = await _context.StructureDefinitions
            .AsNoTracking()
            .Include(e => e.Levels)
            .FirstOrDefaultAsync(e => e.Id == id);
        return entity != null ? MapToModel(entity) : null;
    }

    public async Task<Guid?> GetDefinitionIdOfLevelAsync(Guid levelId)
    {
        return await _context.StructureLevels
            .Where(e => e.Id == levelId)
            .Select(e => (Guid?)e.DefinitionId)
            .FirstOrDefaultAsync();
    }

    public async Task AddAsync(StructureDefinitionModel definition)
    {
        var entity = new StructureDefinitionEntity
        {
            Id = definition.Id,
            Name = definition.Name,
            CreatedAt = definition.CreatedAt,
            UpdatedAt = definition.UpdatedAt,
        };
        _context.StructureDefinitions.Add(entity);
        // Parents are inserted before children so the self-reference is always satisfied.
        foreach (var level in OrderParentsFirst(definition.Levels))
        {
            _context.StructureLevels.Add(MapToEntity(level));
        }

        await _context.SaveChangesAsync();
    }

    public async Task<bool> UpdateAsync(StructureDefinitionModel definition)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();
        // Serialize complete replacements before reading tracked levels, including unchanged flags.
        await _context.Database.SqlQuery<Guid>(
            $"SELECT id AS \"Value\" FROM structure_definitions WHERE id = {definition.Id} FOR UPDATE").ToListAsync();
        var entity = await _context.StructureDefinitions
            .Include(e => e.Levels)
            .FirstOrDefaultAsync(e => e.Id == definition.Id);
        if (entity == null) return false;

        entity.Name = definition.Name;
        entity.UpdatedAt = definition.UpdatedAt;

        var wanted = definition.Levels.ToDictionary(level => level.Id);
        var existing = entity.Levels.ToDictionary(level => level.Id);

        // 1. Detach every surviving level from its parent and drop removed levels, so neither a
        //    removed parent nor a new parent order can violate the restrict self-reference.
        foreach (var level in entity.Levels)
        {
            level.ParentLevelId = null;
        }
        await _context.SaveChangesAsync();

        var removed = entity.Levels.Where(level => !wanted.ContainsKey(level.Id)).ToList();
        _context.StructureLevels.RemoveRange(removed);
        await _context.SaveChangesAsync();

        // 2. Insert new levels and apply names, order and parents.
        foreach (var level in definition.Levels)
        {
            if (existing.TryGetValue(level.Id, out var current))
            {
                current.Name = level.Name;
                current.SortOrder = level.SortOrder;
                current.Required = level.Required;
            }
            else
            {
                _context.StructureLevels.Add(new StructureLevelEntity
                {
                    Id = level.Id,
                    DefinitionId = definition.Id,
                    Name = level.Name,
                    SortOrder = level.SortOrder,
                    Required = level.Required,
                });
            }
        }
        await _context.SaveChangesAsync();

        foreach (var level in definition.Levels)
        {
            var tracked = await _context.StructureLevels.FindAsync(level.Id);
            tracked!.ParentLevelId = level.ParentLevelId;
        }
        await _context.SaveChangesAsync();

        await transaction.CommitAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();
        var entity = await _context.StructureDefinitions
            .Include(e => e.Levels)
            .FirstOrDefaultAsync(e => e.Id == id);
        if (entity == null) return false;

        // Child levels restrict their parents; detach them before the cascade removes all.
        foreach (var level in entity.Levels)
        {
            level.ParentLevelId = null;
        }
        await _context.SaveChangesAsync();

        _context.StructureDefinitions.Remove(entity);
        await _context.SaveChangesAsync();
        await transaction.CommitAsync();
        return true;
    }

    public async Task<HashSet<Guid>> GetUsedLevelIdsAsync(Guid definitionId)
    {
        var used = await _context.DocumentStructureNodes
            .Where(node => _context.StructureLevels
                .Any(level => level.Id == node.LevelId && level.DefinitionId == definitionId))
            .Select(node => node.LevelId)
            .Distinct()
            .ToListAsync();
        return [.. used];
    }

    private static IEnumerable<StructureLevelModel> OrderParentsFirst(IReadOnlyCollection<StructureLevelModel> levels)
    {
        var emitted = new HashSet<Guid>();
        var pending = levels.ToList();
        while (pending.Count > 0)
        {
            var ready = pending
                .Where(level => level.ParentLevelId is not { } parent || emitted.Contains(parent) || levels.All(l => l.Id != parent))
                .ToList();
            if (ready.Count == 0)
            {
                // Cycles are rejected by the service before reaching the repository.
                throw new InvalidOperationException("Structure levels contain a parent cycle.");
            }

            foreach (var level in ready)
            {
                emitted.Add(level.Id);
                pending.Remove(level);
                yield return level;
            }
        }
    }

    private static StructureLevelEntity MapToEntity(StructureLevelModel model) => new()
    {
        Id = model.Id,
        DefinitionId = model.DefinitionId,
        Name = model.Name,
        ParentLevelId = model.ParentLevelId,
        SortOrder = model.SortOrder,
        Required = model.Required,
    };

    private static StructureDefinitionModel MapToModel(StructureDefinitionEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
        Levels = entity.Levels
            .OrderBy(level => level.SortOrder)
            .Select(level => new StructureLevelModel
            {
                Id = level.Id,
                DefinitionId = level.DefinitionId,
                Name = level.Name,
                ParentLevelId = level.ParentLevelId,
                SortOrder = level.SortOrder,
                Required = level.Required,
            })
            .ToList(),
    };
}
