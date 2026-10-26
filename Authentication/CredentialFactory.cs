using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;

namespace BcDeployment;

public static class CredentialFactory
{
    public static TokenCredential Create(AdminCenterOptions options) =>
        options.AuthMode.ToUpperInvariant() switch
        {
            "CLIENTSECRET" => new ClientSecretCredential(
                Required(options.TenantId, "BC_TENANT_ID"),
                Required(options.ClientId, "BC_CLIENT_ID"),
                Required(options.ClientSecret, "BC_CLIENT_SECRET")),
            "CERTIFICATE" => CreateCertificateCredential(options),
            "MANAGEDIDENTITY" => string.IsNullOrWhiteSpace(options.ClientId)
                ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
                : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.ClientId)),
            _ => throw new InvalidOperationException(
                "BC_AUTH_MODE must be ClientSecret, Certificate, or ManagedIdentity.")
        };

    private static ClientCertificateCredential CreateCertificateCredential(AdminCenterOptions options)
    {
        var certificatePath = Path.GetFullPath(Required(
            options.CertificatePath,
            "BC_CLIENT_CERTIFICATE_PATH"));
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            certificatePath,
            options.CertificatePassword,
            X509KeyStorageFlags.EphemeralKeySet);

        return new ClientCertificateCredential(
            Required(options.TenantId, "BC_TENANT_ID"),
            Required(options.ClientId, "BC_CLIENT_ID"),
            certificate);
    }

    private static string Required(string? value, string variableName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{variableName} is required for the selected authentication mode.")
            : value;
}
