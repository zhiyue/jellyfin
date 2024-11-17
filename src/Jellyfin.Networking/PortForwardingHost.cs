using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mono.Nat;

namespace Jellyfin.Networking;

/// <summary>
/// <see cref="IHostedService"/> responsible for UPnP port forwarding.
/// </summary>
public sealed class PortForwardingHost : IHostedService, IDisposable
{
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<PortForwardingHost> _logger;
    private readonly IServerConfigurationManager _config;
    private readonly ConcurrentDictionary<IPEndPoint, byte> _createdRules = new();

    private Timer? _timer;
    private string? _configIdentifier;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PortForwardingHost"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="appHost">The application host.</param>
    /// <param name="config">The configuration manager.</param>
    public PortForwardingHost(
        ILogger<PortForwardingHost> logger,
        IServerApplicationHost appHost,
        IServerConfigurationManager config)
    {
        _logger = logger;
        _appHost = appHost;
        _config = config;
    }

    private string GetConfigIdentifier()
    {
        const char Separator = '|';
        var config = _config.GetNetworkConfiguration();

        return new StringBuilder(32)
            .Append(config.EnableUPnP).Append(Separator)
            .Append(config.PublicHttpPort).Append(Separator)
            .Append(config.PublicHttpsPort).Append(Separator)
            .Append(_appHost.HttpPort).Append(Separator)
            .Append(_appHost.HttpsPort).Append(Separator)
            .Append(_appHost.ListenWithHttps).Append(Separator)
            .Append(config.EnableRemoteAccess).Append(Separator)
            .ToString();
    }

    private void OnConfigurationUpdated(object? sender, EventArgs e)
    {
        var oldConfigIdentifier = _configIdentifier;
        _configIdentifier = GetConfigIdentifier();

        if (!string.Equals(_configIdentifier, oldConfigIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            Stop();
            Start();
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();

        _config.ConfigurationUpdated += OnConfigurationUpdated;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();

        return Task.CompletedTask;
    }

    private void Start()
    {
        var config = _config.GetNetworkConfiguration();
        if (!config.EnableUPnP || !config.EnableRemoteAccess)
        {
            return;
        }

        _logger.LogInformation("Starting NAT discovery");

        NatUtility.DeviceFound += OnNatUtilityDeviceFound;
        NatUtility.StartDiscovery();

        _timer?.Dispose();
        _timer = new Timer(_ => _createdRules.Clear(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    private void Stop()
    {
        _logger.LogInformation("Stopping NAT discovery");

        NatUtility.StopDiscovery();
        NatUtility.DeviceFound -= OnNatUtilityDeviceFound;

        _timer?.Dispose();
        _timer = null;
    }

    private async void OnNatUtilityDeviceFound(object? sender, DeviceEventArgs e)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            // On some systems the device discovered event seems to fire repeatedly
            // This check will help ensure we're not trying to port map the same device over and over
            if (!_createdRules.TryAdd(e.Device.DeviceEndpoint, 0))
            {
                return;
            }

            await Task.WhenAll(CreatePortMaps(e.Device)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating port forwarding rules");
        }
    }

    private IEnumerable<Task> CreatePortMaps(INatDevice device)
    {
        var config = _config.GetNetworkConfiguration();
        yield return CreatePortMap(device, _appHost.HttpPort, config.PublicHttpPort);

        if (_appHost.ListenWithHttps)
        {
            yield return CreatePortMap(device, _appHost.HttpsPort, config.PublicHttpsPort);
        }
    }

    private async Task CreatePortMap(INatDevice device, int privatePort, int publicPort)
    {
        _logger.LogDebug(
            "Creating port map on local port {LocalPort} to public port {PublicPort} with device {DeviceEndpoint}",
            privatePort,
            publicPort,
            device.DeviceEndpoint);

        try
        {
            var mapping = new Mapping(Protocol.Tcp, privatePort, publicPort, 0, _appHost.Name);
            await device.CreatePortMapAsync(mapping).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error creating port map on local port {LocalPort} to public port {PublicPort} with device {DeviceEndpoint}.",
                privatePort,
                publicPort,
                device.DeviceEndpoint);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _config.ConfigurationUpdated -= OnConfigurationUpdated;

        _timer?.Dispose();
        _timer = null;

        _disposed = true;
    }
}
