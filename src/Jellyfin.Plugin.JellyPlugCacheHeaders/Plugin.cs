using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyPlugCacheHeaders;

public class Plugin : BasePlugin<BasePluginConfiguration>
{
    public static Plugin? Instance { get; private set; }

    public override string Name => "JellyPlug Cache Headers";

    public override string Description => "Long-lived Cache-Control on the version-busted /JavaScriptInjector/*.js, 1h + ETag on /Branding/Css, and high-quality brotli/gzip so wire size beats the server's on-the-fly compression.";

    public override Guid Id => Guid.Parse("c1d2e3f4-a5b6-47c8-9d0e-1f2a3b4c5d60");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }
}

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IStartupFilter, CacheHeaderStartupFilter>();
    }
}
