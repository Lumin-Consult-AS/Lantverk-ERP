namespace Erp.Core.Domain.Apps;

public sealed class AppManifestValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public AppManifestValidationException(string message)
        : base(message)
    {
        Errors = [message];
    }

    public AppManifestValidationException(IReadOnlyList<string> errors)
        : base(string.Join(" ", errors))
    {
        Errors = errors;
    }
}

public sealed class AppPackageSignatureException(string message) : Exception(message);

public sealed class AppDependencyException(string message) : Exception(message);

