using System;
using Microsoft.EntityFrameworkCore;
using Doctheca.Database;

namespace Doctheca.Tests.TestHelpers;

public static class InMemoryDbContextFactory
{
    public static DocthecaDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<DocthecaDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new DocthecaDbContext(options);
    }
}
