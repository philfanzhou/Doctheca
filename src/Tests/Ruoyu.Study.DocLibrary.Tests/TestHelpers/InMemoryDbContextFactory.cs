using System;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocLibrary.Database;

namespace Ruoyu.Study.DocLibrary.Tests.TestHelpers;

public static class InMemoryDbContextFactory
{
    public static DocLibraryDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<DocLibraryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new DocLibraryDbContext(options);
    }
}
