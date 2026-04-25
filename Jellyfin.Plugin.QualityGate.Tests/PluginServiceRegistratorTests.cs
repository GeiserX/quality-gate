using System;
using Jellyfin.Plugin.QualityGate.Filters;
using Jellyfin.Plugin.QualityGate.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace Jellyfin.Plugin.QualityGate.Tests;

public class PluginServiceRegistratorTests
{
    [Fact]
    public void RegisterServices_AddsMediaSourceResultFilter()
    {
        var registrator = new PluginServiceRegistrator();
        var services = new ServiceCollection();
        var appHost = new Mock<IServerApplicationHost>();

        registrator.RegisterServices(services, appHost.Object);

        // Verify MediaSourceResultFilter is registered as scoped
        var filterDescriptor = Assert.Single(services, s => s.ServiceType == typeof(MediaSourceResultFilter));
        Assert.Equal(ServiceLifetime.Scoped, filterDescriptor.Lifetime);
    }

    [Fact]
    public void RegisterServices_AddsIntroProvider()
    {
        var registrator = new PluginServiceRegistrator();
        var services = new ServiceCollection();
        var appHost = new Mock<IServerApplicationHost>();

        registrator.RegisterServices(services, appHost.Object);

        var introDescriptor = Assert.Single(services, s => s.ServiceType == typeof(IIntroProvider));
        Assert.Equal(ServiceLifetime.Singleton, introDescriptor.Lifetime);
    }

    [Fact]
    public void RegisterServices_PostConfiguresMvcOptions()
    {
        var registrator = new PluginServiceRegistrator();
        var services = new ServiceCollection();
        var appHost = new Mock<IServerApplicationHost>();

        registrator.RegisterServices(services, appHost.Object);

        // PostConfigure registers an IPostConfigureOptions<MvcOptions>
        Assert.Contains(services, s =>
            s.ServiceType == typeof(IPostConfigureOptions<MvcOptions>));
    }
}
