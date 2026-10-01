using System.Net.Http.Headers;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace GZCTF.Discord;

/// <summary>
/// DI wiring for the Discord integration. Loads and validates <c>discord.yml</c> and, when usable,
/// registers the queue, notifier, typed REST client and background worker. When disabled it registers
/// only a <see cref="NullDiscordNotifier" /> so hook points work unchanged.
/// </summary>
internal static class DiscordServiceExtensions
{
    private const int QueueCapacity = 1000;

    internal static IServiceCollection AddDiscordIntegration(this IServiceCollection services)
    {
        var path = DiscordConfigLoader.ResolveConfigPath();
        var config = DiscordConfigLoader.Load(path, Log.Logger);

        if (config is null || !config.AnyEnabled)
        {
            services.AddSingleton<IDiscordNotifier, NullDiscordNotifier>();
            return services;
        }

        var channel = Channel.CreateBounded<DiscordNotification>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        services.AddSingleton(config);
        services.AddSingleton(channel.Reader);
        services.AddSingleton(channel.Writer);
        services.AddSingleton<IDiscordNotifier, DiscordNotifier>();

        services.AddHttpClient<DiscordApiClient>(client =>
        {
            client.BaseAddress = new Uri("https://discord.com/api/v10/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", config.BotToken);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GZCTF-Discord (https://github.com/GZTimeWalker/GZCTF, 1.0)");
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        services.AddHostedService<DiscordNotificationService>();

        return services;
    }
}
