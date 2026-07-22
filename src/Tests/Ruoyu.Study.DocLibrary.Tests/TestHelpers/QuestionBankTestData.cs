using System;
using Ruoyu.Study.DocLibrary.Database;
using Ruoyu.Study.DocLibrary.Database.Entities;
using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Tests.TestHelpers;

public static class QuestionBankTestData
{
    public static (DocumentParseEntity parse, DocumentFileEntity file) SeedParsedParse(
        DocLibraryDbContext context,
        string fileName = "test.pdf",
        string modelVersion = "vlm",
        DateTimeOffset? parsedAt = null)
    {
        var file = new DocumentFileEntity
        {
            Id = Guid.NewGuid(),
            FileName = fileName,
            FilePath = "uploads/" + fileName,
            ContentType = "application/pdf",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        context.DocumentFiles.Add(file);

        var parse = new DocumentParseEntity
        {
            Id = Guid.NewGuid(),
            DocumentFileId = file.Id,
            ModelVersion = modelVersion,
            Status = DocumentParseStatus.Parsed,
            ParsedAt = parsedAt ?? DateTimeOffset.UtcNow,
        };
        context.DocumentParses.Add(parse);
        context.SaveChanges();
        return (parse, file);
    }

    public static DocumentParseBlockEntity AddBlock(
        DocLibraryDbContext context,
        Guid parseId,
        int pageId,
        int sortIndex,
        string blockType,
        string? textContent = null,
        Guid? imageId = null,
        string blockData = "{}")
    {
        var block = new DocumentParseBlockEntity
        {
            Id = Guid.NewGuid(),
            ParseId = parseId,
            PageId = pageId,
            SortIndex = sortIndex,
            BlockType = blockType,
            TextContent = textContent,
            ImageId = imageId,
            BlockData = blockData,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        context.DocumentParseBlocks.Add(block);
        context.SaveChanges();
        return block;
    }

    public static DocumentParseImageEntity AddImage(
        DocLibraryDbContext context,
        Guid parseId,
        string imageName = "img.jpg",
        string imagePath = "images/img.jpg",
        string contentType = "image/jpeg")
    {
        var image = new DocumentParseImageEntity
        {
            Id = Guid.NewGuid(),
            ParseId = parseId,
            ImageName = imageName,
            ImagePath = imagePath,
            ContentType = contentType,
        };
        context.DocumentParseImages.Add(image);
        context.SaveChanges();
        return image;
    }
}
