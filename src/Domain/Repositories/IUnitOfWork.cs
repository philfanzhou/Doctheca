using System.Threading.Tasks;

namespace Doctheca.Domain.Repositories;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync();
    void ClearChangeTracker();
}
