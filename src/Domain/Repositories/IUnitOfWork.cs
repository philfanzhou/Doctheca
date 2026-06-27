using System.Threading.Tasks;

namespace Ruoyu.Study.DocLibrary.Domain.Repositories;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync();
    void ClearChangeTracker();
}
