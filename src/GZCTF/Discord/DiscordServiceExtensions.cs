using System.Net.Http.Headers;
using System.Threading.Channels;
using GZCTF.Discord.ActivityLog;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace GZCTF.Discord;

/// <summary>
/// DI wiring for the Discord integration (first blood, cheat alerts, activity log). Loads and validates
/// <c>discord.yml</c> and, when usable, registers the queues, notifiers, typed REST client and background
/// workers. When a feature is disabled it registers the matching <c>Null*</c> implementation so hook
/// points work unchanged.
/// </summary>
internal static class DiscordServiceExtensions
{
    private const int QueueCapacity = 1000;
    private const int ActivityQueueCapacity = 5000;

    internal static IServiceCollection AddDiscordIntegration(this IServiceCollection services)
    {
        var path = DiscordConfigLoader.ResolveConfigPath();
        var config = DiscordConfigLoader.Load(path, Log.Logger);

        if (config is null || !config.AnyEnabled)
        {
            services.AddSingleton<IDiscordNotifier, NullDiscordNotifier>();
            services.AddSingleton<IActivityLogger, NullActivityLogger>();
            return services;
        }

        services.AddSingleton(config);

        services.AddHttpClient<DiscordApiClient>(client =>
        {
            client.BaseAddress = new Uri("https://discord.com/api/v10/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", config.BotToken);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "GZCTF-Discord (https://github.com/GZTimeWalker/GZCTF, 1.0)");
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        AddNotifications(services, config);
        AddActivityLog(services, config);

        return services;
    }

    private static void AddNotifications(IServiceCollection services, DiscordConfig config)
    {
        var channel = Channel.CreateBounded<DiscordNotification>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        services.AddSingleton(channel.Reader);
        services.AddSingleton(channel.Writer);
        services.AddSingleton<IDiscordNotifier, DiscordNotifier>();
        services.AddHostedService<DiscordNotificationService>();
    }

    private static void AddActivityLog(IServiceCollection services, DiscordConfig config)
    {
        if (config.ActivityLog is null)
        {
            services.AddSingleton<IActivityLogger, NullActivityLogger>();
            return;
        }

        var raw = Channel.CreateBounded<RawActivityEvent>(new BoundedChannelOptions(ActivityQueueCapacity)
        {
            // Wait mode + TryWrite is non-blocking: a full queue makes TryWrite return false, so the
            // logger drops with a warning and never blocks the originating container/flag operation.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        var reports = Channel.CreateBounded<ReportRequest>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        services.AddSingleton(raw.Reader);
        services.AddSingleton(raw.Writer);
        services.AddSingleton(reports.Reader);
        services.AddSingleton(reports.Writer);

        services.AddSingleton<IActivityLogger, ActivityLogger>();
        services.AddSingleton<ActivityThreadManager>();
        services.AddScoped<IActivityLogRepository, ActivityLogRepository>();
        services.AddScoped<ActivityReportService>();

        services.AddHostedService<ActivityLogService>();
        services.AddHostedService<ActivitySummaryService>();
    }
}
