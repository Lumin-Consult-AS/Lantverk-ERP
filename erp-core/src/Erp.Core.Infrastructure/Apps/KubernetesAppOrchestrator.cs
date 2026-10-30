using System.Collections.Concurrent;
using System.Text;
using Erp.Core.Domain.Apps;

namespace Erp.Core.Infrastructure.Apps;

public sealed record AppContainerDeploymentSpec(
    string AppId,
    string Version,
    string DeploymentName,
    string ServiceUrl,
    string Image,
    string CpuLimit,
    string MemoryLimit,
    IReadOnlyList<string> AllowedEgress,
    string KubernetesDeploymentYaml,
    string KubernetesNetworkPolicyYaml);

public sealed record AppGrpcEndpointRegistration(
    string AppId,
    string Version,
    string Address,
    Func<HttpMessageHandler>? ChannelHandlerFactory = null);

/// <summary>
/// Orchestrates one shared container per <c>(appId, version)</c> across all tenants via the Kubernetes API,
/// applying CPU/memory resource limits and generating strict <c>NetworkPolicy</c> egress whitelists
/// from the app's manifest (<c>runtime.egress</c>).
/// </summary>
public sealed class KubernetesAppOrchestrator
{
    private readonly ConcurrentDictionary<string, AppContainerDeploymentSpec> _deployments = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AppGrpcEndpointRegistration> _endpoints = new(StringComparer.OrdinalIgnoreCase);

    public AppContainerDeploymentSpec EnsureContainerRunning(AppManifest manifest)
    {
        if (manifest.Runtime is null || string.IsNullOrWhiteSpace(manifest.Runtime.Image))
        {
            throw new InvalidOperationException($"App '{manifest.Id}' is declarative and does not define a runtime container.");
        }

        var key = BuildKey(manifest.Id, manifest.Version);
        return _deployments.GetOrAdd(key, _ =>
        {
            var sanitizedName = SanitizeK8sName($"erp-app-{manifest.Id}-{manifest.Version}");
            var cpu = string.IsNullOrWhiteSpace(manifest.Runtime.Resources?.Cpu) ? "250m" : manifest.Runtime.Resources.Cpu!;
            var memory = string.IsNullOrWhiteSpace(manifest.Runtime.Resources?.Memory) ? "256Mi" : manifest.Runtime.Resources.Memory!;
            var egress = manifest.Runtime.Egress.ToList();
            var serviceUrl = $"http://{sanitizedName}.erp-apps.svc.cluster.local:50051";

            var deploymentYaml = GenerateDeploymentYaml(sanitizedName, manifest.Id, manifest.Version, manifest.Runtime.Image, cpu, memory);
            var networkPolicyYaml = GenerateNetworkPolicyYaml(sanitizedName, manifest.Id, manifest.Version, egress);

            return new AppContainerDeploymentSpec(
                AppId: manifest.Id,
                Version: manifest.Version,
                DeploymentName: sanitizedName,
                ServiceUrl: serviceUrl,
                Image: manifest.Runtime.Image,
                CpuLimit: cpu,
                MemoryLimit: memory,
                AllowedEgress: egress,
                KubernetesDeploymentYaml: deploymentYaml,
                KubernetesNetworkPolicyYaml: networkPolicyYaml);
        });
    }

    public AppContainerDeploymentSpec? GetDeploymentSpec(string appId, string version)
        => _deployments.TryGetValue(BuildKey(appId, version), out var spec) ? spec : null;

    public IReadOnlyList<AppContainerDeploymentSpec> GetAllDeployments()
        => _deployments.Values.OrderBy(d => d.AppId).ThenBy(d => d.Version).ToList();

    /// <summary>
    /// Registers or overrides the live gRPC endpoint for a specific <c>(appId, version)</c>
    /// (or <c>version = "*"</c> for all versions of <c>appId</c>).
    /// </summary>
    public void RegisterEndpoint(
        string appId,
        string version,
        string address,
        Func<HttpMessageHandler>? channelHandlerFactory = null)
    {
        var registration = new AppGrpcEndpointRegistration(appId, version, address, channelHandlerFactory);
        _endpoints[BuildKey(appId, version)] = registration;
    }

    public void UnregisterEndpoint(string appId, string version = "*")
    {
        _endpoints.TryRemove(BuildKey(appId, version), out _);
    }

    public AppGrpcEndpointRegistration? ResolveEndpoint(string appId, string version)
    {
        if (_endpoints.TryGetValue(BuildKey(appId, version), out var exact))
        {
            return exact;
        }

        if (_endpoints.TryGetValue(BuildKey(appId, "*"), out var wildcard))
        {
            return wildcard;
        }

        return null;
    }

    private static string BuildKey(string appId, string version)
        => $"{appId.Trim().ToLowerInvariant()}@{version.Trim().ToLowerInvariant()}";

    private static string SanitizeK8sName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        }
        return sb.ToString().Trim('-');
    }

    private static string GenerateDeploymentYaml(
        string deploymentName,
        string appId,
        string version,
        string image,
        string cpu,
        string memory)
    {
        return $"""
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: {deploymentName}
              namespace: erp-apps
              labels:
                erp.laftverk.no/app-id: "{appId}"
                erp.laftverk.no/app-version: "{version}"
            spec:
              replicas: 1
              selector:
                matchLabels:
                  erp.laftverk.no/deployment: {deploymentName}
              template:
                metadata:
                  labels:
                    erp.laftverk.no/deployment: {deploymentName}
                    erp.laftverk.no/app-id: "{appId}"
                    erp.laftverk.no/app-version: "{version}"
                spec:
                  containers:
                    - name: app
                      image: {image}
                      ports:
                        - containerPort: 50051
                          protocol: TCP
                      resources:
                        requests:
                          cpu: "{cpu}"
                          memory: "{memory}"
                        limits:
                          cpu: "{cpu}"
                          memory: "{memory}"
            ---
            apiVersion: v1
            kind: Service
            metadata:
              name: {deploymentName}
              namespace: erp-apps
            spec:
              selector:
                erp.laftverk.no/deployment: {deploymentName}
              ports:
                - port: 50051
                  targetPort: 50051
                  protocol: TCP
            """;
    }

    private static string GenerateNetworkPolicyYaml(
        string deploymentName,
        string appId,
        string version,
        IReadOnlyList<string> allowedEgress)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $$"""
            apiVersion: networking.k8s.io/v1
            kind: NetworkPolicy
            metadata:
              name: {{deploymentName}}-netpol
              namespace: erp-apps
              labels:
                erp.laftverk.no/app-id: "{{appId}}"
                erp.laftverk.no/app-version: "{{version}}"
            spec:
              podSelector:
                matchLabels:
                  erp.laftverk.no/deployment: {{deploymentName}}
              policyTypes:
                - Ingress
                - Egress
              ingress:
                - from:
                    - namespaceSelector:
                        matchLabels:
                          kubernetes.io/metadata.name: erp-core
                  ports:
                    - protocol: TCP
                      port: 50051
              egress:
                - to:
                    - namespaceSelector:
                        matchLabels:
                          kubernetes.io/metadata.name: erp-core
                  ports:
                    - protocol: TCP
                      port: 5000
                - to:
                    - namespaceSelector: {}
                  ports:
                    - protocol: UDP
                      port: 53
                    - protocol: TCP
                      port: 53
            """);

        foreach (var entry in allowedEgress)
        {
            var parts = entry.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 443;
            sb.AppendLine(
                $"""
                    # Whitelisted egress host: {host}:{port}
                    - ports:
                        - protocol: TCP
                          port: {port}
                """);
        }

        return sb.ToString().TrimEnd();
    }
}
