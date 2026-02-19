namespace PrintNest.Domain.Errors;

/// <summary>
/// Thrown by domain and application logic when a business rule is violated.
///
/// The error handling middleware (Api/Middleware/ErrorHandlingMiddleware.cs) catches this
/// and converts it to the standard API error envelope with the appropriate HTTP status code.
///
/// Usage:
///   throw new DomainException(ErrorCodes.JobStateInvalid, "Job is not in Paid state.", 409);
/// </summary>
public sealed class DomainException : Exception
{
    /// <summary>Machine-readable error code. One of the constants in <see cref="ErrorCodes"/>.</summary>
    public string Code { get; }

    /// <summary>HTTP status code to return to the caller.</summary>
    public int HttpStatus { get; }

    public DomainException(string code, string message, int httpStatus = 400)
        : base(message)
    {
        Code = code;
        HttpStatus = httpStatus;
    }
}
