namespace Doctheca.Domain.Exceptions;

public class DocthecaValidationException : Exception
{
    public DocthecaValidationException(string message) : base(message) { }
}
