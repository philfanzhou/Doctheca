using System.Threading.Tasks;
using Ruoyu.Study.DocLibrary.Domain.Repositories;

namespace Ruoyu.Study.DocLibrary.Database.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly DocLibraryDbContext _dbContext;

    public UnitOfWork(DocLibraryDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<int> SaveChangesAsync()
    {
        return await _dbContext.SaveChangesAsync();
    }

    public void ClearChangeTracker()
    {
        _dbContext.ChangeTracker.Clear();
    }
}
