namespace BcDeployment;

public sealed class AdminCenterOptions
{
    public Uri BaseUri { get; init; } = new("https://api.businesscentral.dynamics.com/");

    public string Scope { get; init; } = "https://api.businesscentral.dynamics.com/.default";

    public string ApiVersion { get; init; } = "v2.29";

    public string ApplicationFamily { get; init; } = "BusinessCentral";

    public required string AuthMode { get; init; }

    public string? TenantId { get; init; }

    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public string? CertificatePath { get; init; }

    public string? CertificatePassword { get; init; }

    public required IReadOnlyList<string> AllowedAppRoots { get; init; }

    public IReadOnlyList<string> AllowedSymbolRoots { get; init; } = [];

    /// <summary>
    /// Master switch for Production targets. Callers must additionally confirm each Production deployment.
    /// </summary>
    public bool AllowProductionDeployment { get; init; }

    public string JournalDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BcDeployment",
        "journal");

    public static AdminCenterOptions FromEnvironment()
    {
        var certificatePath = Environment.GetEnvironmentVariable("BC_CLIENT_CERTIFICATE_PATH");
        var clientSecret = Environment.GetEnvironmentVariable("BC_CLIENT_SECRET");
        var authMode = Environment.GetEnvironmentVariable("BC_AUTH_MODE")
            ?? (certificatePath is not null ? "Certificate" : "ClientSecret");
        var roots = (Environment.GetEnvironmentVariable("BC_ALLOWED_APP_ROOTS")
                ?? Directory.GetCurrentDirectory())
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .ToArray();
        var allowedSymbolRoots = (Environment.GetEnvironmentVariable("BC_ALLOWED_SYMBOL_ROOTS")
                ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .ToArray();

        if (roots.Length == 0)
        {
            throw new InvalidOperationException("BC_ALLOWED_APP_ROOTS must contain at least one directory.");
        }

        return new AdminCenterOptions
        {
            AuthMode = authMode,
            TenantId = Environment.GetEnvironmentVariable("BC_TENANT_ID"),
            ClientId = Environment.GetEnvironmentVariable("BC_CLIENT_ID"),
            ClientSecret = clientSecret,
            CertificatePath = certificatePath,
            CertificatePassword = Environment.GetEnvironmentVariable("BC_CLIENT_CERTIFICATE_PASSWORD"),
            AllowedAppRoots = roots,
            AllowedSymbolRoots = allowedSymbolRoots,
            AllowProductionDeployment = ParseBoolean(
                Environment.GetEnvironmentVariable("BC_ALLOW_PRODUCTION_DEPLOYMENT")),
            JournalDirectory = Path.GetFullPath(
                Environment.GetEnvironmentVariable("BC_DEPLOYMENT_JOURNAL_DIRECTORY")
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BcDeployment",
                        "journal"))
        };
    }

    private static bool ParseBoolean(string? value) =>
        value is not null
        && (value.Trim() is "1"
            || bool.TryParse(value.Trim(), out var parsed) && parsed);
}
