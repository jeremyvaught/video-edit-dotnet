using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace VideoEdit.Services;

public sealed record ListenConflict(string Url, string? ProjectDirectory);

public sealed record ListenPlan(string? Url, ListenConflict? Conflict);

public static class ReviewListen
{
    public const int MaxPortHops = 20;

    public static readonly string[] OneShotFlags =
    [
        "--prepare-only",
        "--import-findings",
        "--set-narration-voice",
        "--align-script",
        "--add-source",
        "--apply-pickup",
        "--prepare-pickups",
        "--insert-generated-scene",
        "--build-rough",
        "--build-splices",
        "--analyze-vad",
        "--snap-pending",
    ];

    public static bool WillServe(IReadOnlyList<string> args)
    {
        if (args.Contains("--prepare-only", StringComparer.Ordinal))
        {
            return false;
        }

        if (args.Contains("--prepare", StringComparer.Ordinal))
        {
            return true;
        }

        return !OneShotFlags.Any(flag => args.Contains(flag, StringComparer.Ordinal));
    }

    public static bool TryParse(string listenUrl, out string host, out int port)
    {
        host = "";
        port = 0;
        if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || uri.Port <= 0)
        {
            return false;
        }

        host = uri.HostNameType == UriHostNameType.IPv6 ? uri.DnsSafeHost : uri.Host;
        port = uri.Port;
        return IsLoopback(host);
    }

    public static string Format(string host, int port)
    {
        var inner = StripBrackets(host);
        var authority = IsIpv6Address(inner) ? $"[{inner}]" : inner;
        return string.Create(CultureInfo.InvariantCulture, $"http://{authority}:{port}");
    }

    public static bool PortIsFree(string host, int port)
    {
        var addresses = BindAddresses(host);
        return addresses.Count > 0 && addresses.All(address => CanBind(address, port));
    }

    public static async Task<ListenPlan> ResolveAsync(
        string listenUrl,
        CancellationToken cancellationToken = default)
    {
        if (!TryParse(listenUrl, out var host, out var port))
        {
            return new ListenPlan(null, null);
        }

        if (PortIsFree(host, port))
        {
            return new ListenPlan(Format(host, port), null);
        }

        var occupant = await OccupyingProjectAsync(Format(host, port), cancellationToken);
        var conflict = new ListenConflict(Format(host, port), occupant);
        for (var candidate = port + 1; candidate <= port + MaxPortHops; candidate++)
        {
            if (PortIsFree(host, candidate))
            {
                return new ListenPlan(Format(host, candidate), conflict);
            }
        }

        return new ListenPlan(null, conflict);
    }

    public static string ConflictMessage(ListenConflict conflict)
    {
        var who = string.IsNullOrWhiteSpace(conflict.ProjectDirectory)
            ? "an unknown project"
            : conflict.ProjectDirectory;
        return $"{conflict.Url} is already serving {who}";
    }

    public static async Task<string?> OccupyingProjectAsync(
        string listenUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            var diagnostics = await client.GetFromJsonAsync<ListenDiagnostics>(
                listenUrl.TrimEnd('/') + "/api/diagnostics",
                cancellationToken);
            return ProjectDirectoryFromPath(diagnostics?.ProjectPath);
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or UriFormatException
            or System.Text.Json.JsonException
            or NotSupportedException)
        {
            return null;
        }
    }

    public static string? ProjectDirectoryFromPath(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return null;
        }

        var edit = Path.GetDirectoryName(projectPath);
        if (edit is not null && string.Equals(Path.GetFileName(edit), "edit", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(edit);
        }

        return projectPath;
    }

    private static IReadOnlyList<IPAddress> BindAddresses(string host)
    {
        var inner = StripBrackets(host);
        if (string.Equals(inner, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return [IPAddress.Loopback, IPAddress.IPv6Loopback];
        }

        return IPAddress.TryParse(inner, out var address) && IsLoopback(inner)
            ? [address]
            : [];
    }

    private static bool CanBind(IPAddress address, int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(address, port);
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                listener.Server.DualMode = false;
            }

            listener.Start();
            return true;
        }
        catch (SocketException ex) when (IsUnavailableFamily(ex))
        {
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static bool IsUnavailableFamily(SocketException ex) =>
        ex.SocketErrorCode is SocketError.AddressFamilyNotSupported
            or SocketError.ProtocolNotSupported
            or SocketError.AddressNotAvailable;

    private static bool IsLoopback(string host)
    {
        var inner = StripBrackets(host);
        return string.Equals(inner, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(inner, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(inner, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIpv6Address(string host) =>
        IPAddress.TryParse(StripBrackets(host), out var address)
        && address.AddressFamily == AddressFamily.InterNetworkV6;

    private static string StripBrackets(string host) =>
        host.Length >= 2 && host[0] == '[' && host[^1] == ']'
            ? host[1..^1]
            : host;

    private sealed class ListenDiagnostics
    {
        [JsonPropertyName("projectPath")]
        public string? ProjectPath { get; set; }
    }
}
