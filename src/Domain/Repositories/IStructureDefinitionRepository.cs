using Doctheca.Domain.Models.Structure;

namespace Doctheca.Domain.Repositories;

public interface IStructureDefinitionRepository
{
    /// <summary>All definitions with their levels, ordered by name.</summary>
    Task<List<StructureDefinitionModel>> ListAsync();

    Task<StructureDefinitionModel?> GetByIdAsync(Guid id);

    /// <summary>Returns the definition that owns <paramref name="levelId"/>, or null.</summary>
    Task<Guid?> GetDefinitionIdOfLevelAsync(Guid levelId);

    /// <summary>Inserts the definition and its levels in one save.</summary>
    Task AddAsync(StructureDefinitionModel definition);

    /// <summary>
    /// Renames the definition and makes its stored levels equal to <paramref name="definition"/>'s
    /// levels (insert, update, delete) in one transaction. Returns false when it does not exist.
    /// </summary>
    Task<bool> UpdateAsync(StructureDefinitionModel definition);

    /// <summary>Deletes the definition and its levels; documents that chose it are cleared.</summary>
    Task<bool> DeleteAsync(Guid id);

    /// <summary>The ids of this definition's levels that marked structure nodes use.</summary>
    Task<HashSet<Guid>> GetUsedLevelIdsAsync(Guid definitionId);
}
