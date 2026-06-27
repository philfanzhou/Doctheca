namespace Ruoyu.Study.DocLibrary.Domain.Exceptions;

public class DocLibraryValidationException : Exception
{
    public DocLibraryValidationException(string message) : base(message) { }
}
