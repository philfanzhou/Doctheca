using System.Threading.Tasks;

namespace Ruoyu.Study.DocRetrieval.Domain.Repositories;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync();
}
