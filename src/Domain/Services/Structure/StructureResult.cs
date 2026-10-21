namespace Doctheca.Domain.Services.Structure;

public enum StructureResultStatus
{
    Ok,
    NotFound,
    Invalid,
    Conflict,
}

/// <summary>One field-level validation failure; <see cref="Field"/> is a JSON path such as <c>nodes[0].children[1].endSequence</c>.</summary>
public sealed record StructureFieldError(string Field, string Reason);

/// <summary>The outcome of a structure operation, mapped by the endpoints to 200/404/400/409.</summary>
public sealed class StructureResult<T>
{
    private StructureResult(StructureResultStatus status, T? value, string? errorCode, string? message,
        IReadOnlyList<StructureFieldError> errors)
    {
        Status = status;
        Value = value;
        ErrorCode = errorCode;
        Message = message;
        Errors = errors;
    }

    public StructureResultStatus Status { get; }
    public T? Value { get; }
    public string? ErrorCode { get; }
    public string? Message { get; }
    public IReadOnlyList<StructureFieldError> Errors { get; }

    public static StructureResult<T> Ok(T value) => new(StructureResultStatus.Ok, value, null, null, []);

    public static StructureResult<T> NotFound(string errorCode, string message) =>
        new(StructureResultStatus.NotFound, default, errorCode, message, []);

    public static StructureResult<T> Invalid(IReadOnlyList<StructureFieldError> errors) =>
        new(StructureResultStatus.Invalid, default, StructureErrorCodes.Invalid, "Structure validation failed", errors);

    public static StructureResult<T> Conflict(string errorCode, string message) =>
        new(StructureResultStatus.Conflict, default, errorCode, message, []);
}

public static class StructureErrorCodes
{
    public const string Invalid = "DOCTHECA_STRUCTURE_INVALID";
    public const string DefinitionNotFound = "DOCTHECA_STRUCTURE_DEFINITION_NOT_FOUND";
    public const string ParseNotFound = "DOCTHECA_PARSE_NOT_FOUND";
    public const string FileNotFound = "DOCTHECA_FILE_NOT_FOUND";
    public const string NodeNotFound = "DOCTHECA_STRUCTURE_NODE_NOT_FOUND";
    public const string DefinitionInUse = "DOCTHECA_STRUCTURE_DEFINITION_IN_USE";
    public const string LevelInUse = "DOCTHECA_STRUCTURE_LEVEL_IN_USE";
    public const string DocumentStructureInUse = "DOCTHECA_DOCUMENT_STRUCTURE_IN_USE";
}
