using System.Data;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services.Structure;
using Microsoft.EntityFrameworkCore;

namespace Doctheca.Database.Repositories;

/// <summary>Administrator mutations share the existing document-first ownership boundary.</summary>
public sealed class DocumentAuthoringRepository(DocthecaDbContext context) : IDocumentAuthoringRepository
{
    public async Task<StructureResult<DocumentFileModel>> CreateAsync(DocumentMetadataInput input, Guid? createdBy, CancellationToken ct)
    {
        await using var tx = await context.Database.BeginTransactionAsync(ct);
        var errors = ValidateMetadata(input, true);
        await ValidateDefinitionAsync(input, errors, ct);
        if (errors.Count > 0) return StructureResult<DocumentFileModel>.Invalid(errors);
        var file = new DocumentFileEntity { FileName = input.FileName!.Trim(), Subject = input.Subject,
            Grade = input.Grade, Year = input.Year, StructureDefinitionId = input.StructureDefinitionId,
            ContentType = string.Empty, CreatedBy = createdBy };
        context.DocumentFiles.Add(file);
        await context.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return StructureResult<DocumentFileModel>.Ok(Model(file));
    }

    public async Task<StructureResult<DocumentFileModel>> UpdateAsync(Guid id, DocumentMetadataInput input, CancellationToken ct)
    {
        await using var tx = await context.Database.BeginTransactionAsync(ct);
        var file = await StructureOwnership.LockDocumentAsync(context, id, ct);
        if (file is null) return Missing<DocumentFileModel>();
        var errors = ValidateMetadata(input, false);
        await ValidateDefinitionAsync(input, errors, ct);
        if (errors.Count > 0) return StructureResult<DocumentFileModel>.Invalid(errors);
        if (input.SetDefinition && file.StructureDefinitionId != input.StructureDefinitionId &&
            await context.DocumentStructureNodes.AnyAsync(n => n.DocumentFileId == id, ct))
            return StructureResult<DocumentFileModel>.Conflict(StructureErrorCodes.DocumentStructureInUse,
                "Clear current and historical structure nodes before changing the definition");
        if (input.FileName is not null) file.FileName = input.FileName.Trim();
        if (input.Subject is not null) file.Subject = input.Subject;
        if (input.Grade is not null) file.Grade = input.Grade;
        if (input.Year is not null) file.Year = input.Year;
        if (input.SetDefinition) file.StructureDefinitionId = input.StructureDefinitionId;
        file.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return StructureResult<DocumentFileModel>.Ok(Model(file));
    }

    public async Task<DocumentFileModel?> FillMissingAsync(Guid id, string? subject, string? grade, string? year, CancellationToken ct)
    {
        await using var tx = await context.Database.BeginTransactionAsync(ct);
        var file = await StructureOwnership.LockDocumentAsync(context, id, ct);
        if (file is null) return null;
        // The LLM snapshot is never written back. Recheck current values after taking the owner lock.
        if (string.IsNullOrWhiteSpace(file.Subject) && subject?.Length <= 50) file.Subject = subject;
        if (string.IsNullOrWhiteSpace(file.Grade) && grade?.Length <= 20) file.Grade = grade;
        if (string.IsNullOrWhiteSpace(file.Year) && year?.Length <= 10) file.Year = year;
        file.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Model(file);
    }

    public async Task<StructureResult<DocumentFileModel>> AttachSourceAsync(Guid id, Guid sourceId, string mediaType, CancellationToken ct)
    {
        await using var tx = await context.Database.BeginTransactionAsync(ct);
        var file = await StructureOwnership.LockDocumentAsync(context, id, ct);
        if (file is null) return Missing<DocumentFileModel>();
        if (file.StructaDocDocumentId is not null || !string.IsNullOrWhiteSpace(file.FilePath))
            return StructureResult<DocumentFileModel>.Conflict("DOCTHECA_DOCUMENT_SOURCE_EXISTS", "The document already has a source");
        file.StructaDocDocumentId = sourceId;
        file.ContentType = mediaType;
        file.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(ct);
        ct.ThrowIfCancellationRequested();
        await tx.CommitAsync(ct);
        return StructureResult<DocumentFileModel>.Ok(Model(file));
    }

    public async Task<bool> IsSourceReferencedAsync(Guid documentId, Guid sourceId, CancellationToken ct)
    {
        await using var tx = await context.Database.BeginTransactionAsync(ct);
        // Wait for any uncertain original commit before deciding that the uploaded object is unowned.
        await StructureOwnership.LockDocumentAsync(context, documentId, ct);
        return await context.DocumentFiles.AsNoTracking().AnyAsync(f => f.StructaDocDocumentId == sourceId, ct);
    }

    public async Task<DocumentStructureTree?> GetTreeAsync(Guid documentId, CancellationToken ct)
    {
        await using var tx = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var file = await context.DocumentFiles.AsNoTracking().SingleOrDefaultAsync(f => f.Id == documentId, ct);
        return file is null ? null : await ReadTreeAsync(file, ct);
    }

    public Task<List<DocumentStructureNodeModel>> GetCurrentBindingsAsync(Guid parseId) =>
        new DocumentStructureRepository(context).GetCurrentBindingsAsync(parseId);

    public async Task<StructureResult<DocumentTreeSave>> SaveTreeAsync(Guid documentId, IReadOnlyList<DocumentNodeInput> roots, CancellationToken ct)
    {
        await using var tx = await context.Database.BeginTransactionAsync(ct);
        var file = await StructureOwnership.LockDocumentAsync(context, documentId, ct);
        if (file is null) return Missing<DocumentTreeSave>();
        var existing = await context.DocumentStructureNodes.AsNoTracking().Where(n => n.TreeId == file.CurrentStructureTreeId).ToDictionaryAsync(n => n.Id, ct);
        var flat = new List<DocumentStructureNodeModel>();
        var errors = new List<StructureFieldError>();
        var seen = new HashSet<Guid>();
        var paths = new Dictionary<Guid, string>();
        var now = DateTimeOffset.UtcNow;
        void Visit(IReadOnlyList<DocumentNodeInput> siblings, Guid? parent, string path, int depth)
        {
            if (depth > 64 || flat.Count + siblings.Count > 5000) { errors.Add(new(path, "at most 5000 nodes and 64 nested levels are allowed")); return; }
            for (var i = 0; i < siblings.Count; i++)
            {
                var input = siblings[i]; var p = $"{path}[{i}]";
                if (input is null) { errors.Add(new(p, "is required")); continue; }
                var id = input.Id ?? Guid.NewGuid();
                if (!seen.Add(id)) errors.Add(new(p + ".id", "is used by more than one node"));
                paths.TryAdd(id, p);
                if (input.LevelId is null) errors.Add(new(p + ".levelId", "is required"));
                var name = input.Name?.Trim();
                if (string.IsNullOrEmpty(name) || name.Length > 500) errors.Add(new(p + ".name", "must contain 1 to 500 characters"));
                flat.Add(new() { Id = id, DocumentFileId = documentId, TreeId = file.CurrentStructureTreeId ?? Guid.Empty,
                    ParentId = parent, LevelId = input.LevelId ?? Guid.Empty, Name = name ?? string.Empty, SortOrder = i,
                    ParseId = input.ParseId, StartSequence = input.StartSequence, EndSequence = input.EndSequence,
                    CreatedAt = existing.TryGetValue(id, out var old) ? old.CreatedAt : now, UpdatedAt = now });
                Visit(input.Children ?? [], id, p + ".children", depth + 1);
            }
        }
        Visit(roots, null, "nodes", 0);
        errors.AddRange(await DocumentStructureRepository.ValidateNodesAsync(context, file, file.CurrentStructureTreeId, flat, ct, paths));
        var parseIds = flat.Where(n => n.ParseId.HasValue).Select(n => n.ParseId!.Value).Distinct().ToArray();
        if (errors.Count > 0) return StructureResult<DocumentTreeSave>.Invalid(errors);
        if (file.CurrentStructureTreeId is null)
        {
            var tree = new DocumentStructureTreeEntity { DocumentFileId = documentId };
            context.DocumentStructureTrees.Add(tree);
            await context.SaveChangesAsync(ct);
            file.CurrentStructureTreeId = tree.Id;
        }
        var affected = existing.Values.Where(n => n.ParseId.HasValue).Select(n => n.ParseId!.Value).Union(parseIds).ToArray();
        file.StructureBindingGeneration++;
        await DocumentStructureRepository.ReplaceNodesAsync(context, documentId, file.CurrentStructureTreeId.Value, flat, ct);
        await context.SaveChangesAsync(ct);
        var result = await ReadTreeAsync(file, ct);
        ct.ThrowIfCancellationRequested();
        await tx.CommitAsync(ct);
        context.ChangeTracker.Clear();
        return StructureResult<DocumentTreeSave>.Ok(new(result, affected));
    }

    private async Task<DocumentStructureTree> ReadTreeAsync(DocumentFileEntity file, CancellationToken ct)
    {
        var definition = file.StructureDefinitionId is { } id ? await new StructureDefinitionRepository(context).GetByIdAsync(id) : null;
        var nodes = await context.DocumentStructureNodes.AsNoTracking().Where(n => n.TreeId == file.CurrentStructureTreeId)
            .OrderBy(n => n.SortOrder).Select(n => DocumentStructureRepository.MapToModel(n)).ToListAsync(ct);
        var bindings = nodes.Select(n => n.ParseId).Distinct().ToArray();
        return new() { TreeId = file.CurrentStructureTreeId, DocumentFileId = file.Id, FileName = file.FileName,
            ParseId = bindings.Length == 1 ? bindings[0] : null, Definition = definition, Nodes = nodes };
    }

    private async Task ValidateDefinitionAsync(DocumentMetadataInput input, List<StructureFieldError> errors, CancellationToken ct)
    {
        if (!input.SetDefinition || input.StructureDefinitionId is not { } id) return;
        var found = await context.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM structure_definitions WHERE id = {id} FOR SHARE").ToListAsync(ct);
        if (found.Count == 0) errors.Add(new("structureDefinitionId", "structure definition does not exist"));
    }
    private static List<StructureFieldError> ValidateMetadata(DocumentMetadataInput input, bool create)
    {
        var errors = new List<StructureFieldError>();
        if ((create || input.FileName is not null) && (string.IsNullOrWhiteSpace(input.FileName) || input.FileName.Trim().Length > 500))
            errors.Add(new("fileName", "must contain 1 to 500 characters"));
        if (input.Subject?.Length > 50) errors.Add(new("subject", "must be at most 50 characters"));
        if (input.Grade?.Length > 20) errors.Add(new("grade", "must be at most 20 characters"));
        if (input.Year?.Length > 10) errors.Add(new("year", "must be at most 10 characters"));
        return errors;
    }
    private static StructureResult<T> Missing<T>() => StructureResult<T>.NotFound(StructureErrorCodes.FileNotFound, "File not found");
    private static DocumentFileModel Model(DocumentFileEntity f) => new() { Id = f.Id, FileName = f.FileName, FilePath = f.FilePath,
        StructaDocDocumentId = f.StructaDocDocumentId, ContentType = f.ContentType, CreatedBy = f.CreatedBy,
        Subject = f.Subject, Grade = f.Grade, Year = f.Year, StructureDefinitionId = f.StructureDefinitionId,
        CreatedAt = f.CreatedAt, UpdatedAt = f.UpdatedAt };
}
