using OpenSandbox.Config;

namespace OpenClawNet.Sandbox.OpenSandbox;

public sealed class OpenSandboxOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public int DefaultTTL { get; set; } = 300;

    /// <summary>
    /// Builds a <see cref="ConnectionConfig"/> from <see cref="Endpoint"/> and <see cref="ApiKey"/>.
    /// Endpoint format: http[s]://host[:port]
    /// </summary>
    public ConnectionConfig BuildConnectionConfig()
    {
        var uri = new Uri(Endpoint.Trim());
        var protocol = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            ? ConnectionProtocol.Https
            : ConnectionProtocol.Http;

        // ConnectionConfigOptions.Domain expects "host" or "host:port"
        var domain = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

        return new ConnectionConfig(new ConnectionConfigOptions
        {
            Domain = domain,
            Protocol = protocol,
            ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey,
        });
    }
}

