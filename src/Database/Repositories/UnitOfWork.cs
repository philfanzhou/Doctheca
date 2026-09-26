using System.Threading.Tasks;
using Doctheca.Domain.Repositories;

namespace Doctheca.Database.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly DocthecaDbContext _dbContext;

    public UnitOfWork(DocthecaDbContext dbContext)
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
