using System.Threading.Tasks;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;

namespace Ruoyu.Study.DocRetrieval.Database.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly DocRetrievalDbContext _dbContext;

    public UnitOfWork(DocRetrievalDbContext dbContext)
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
