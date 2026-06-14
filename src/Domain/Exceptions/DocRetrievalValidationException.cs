namespace Ruoyu.Study.DocRetrieval.Domain.Exceptions;

public class DocRetrievalValidationException : Exception
{
    public DocRetrievalValidationException(string message) : base(message) { }
}
