using GZCTF.Discord.ActivityLog;
using GZCTF.Models.Request.Admin;
using GZCTF.Repositories.Interface;
using GZCTF.Services.Cache;
using GZCTF.Services.Container.Manager;
using GZCTF.Services.Traffic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace GZCTF.Repositories;

public class ContainerRepository(
    IDistributedCache cache,
    IContainerManager service,
    TrafficRecorderRegistry trafficRegistry,
    IActivityLogger activityLogger,
    ILogger<ContainerRepository> logger,
    AppDbContext context) : RepositoryBase(context), IContainerRepository
{
    public override Task<int> CountAsync(CancellationToken token = default) => Context.Containers.CountAsync(token);

    public Task<Container?> GetContainerById(Guid guid, CancellationToken token = default) =>
        Context.Containers.FirstOrDefaultAsync(i => i.Id == guid, token);

    public Task<Container?> GetContainerWithInstanceById(Guid guid, CancellationToken token = default) =>
        Context.Containers.IgnoreAutoIncludes()
            .Include(c => c.GameInstance).ThenInclude(i => i!.Challenge)
            .Include(c => c.GameInstance).ThenInclude(i => i!.FlagContext)
            .Include(c => c.GameInstance).ThenInclude(i => i!.Participation).ThenInclude(p => p.Team)
            .FirstOrDefaultAsync(i => i.Id == guid, token);

    public async Task<ContainerInstanceModel[]> GetContainerInstances(CancellationToken token = default) =>
        (await Context.Containers
            .Where(c => c.GameInstance != null)
            .Include(c => c.GameInstance).ThenInclude(i => i!.Participation)
            .OrderBy(c => c.StartedAt).ToArrayAsync(token))
        .Select(ContainerInstanceModel.FromContainer)
        .ToArray();

    public Task<Container[]> GetDyingContainers(CancellationToken token = default) =>
        Context.Containers.Where(c => c.ExpectStopAt < DateTimeOffset.UtcNow).ToArrayAsync(token);

    public Task ExtendLifetime(Container container, TimeSpan time, CancellationToken token = default)
    {
        container.ExpectStopAt += time;
        return SaveAsync(token);
    }

    /// <summary>
    /// Resolve the owning game instance key (participation + challenge) for a container, so a destruction
    /// can be attributed in the activity log. Returns null for non-game (exercise) containers or if the
    /// lookup fails; never throws.
    /// </summary>
    private async Task<(int ParticipationId, int ChallengeId)?> ResolveInstanceKey(Container container,
        CancellationToken token)
    {
        try
        {
            var key = await Context.GameInstances.AsNoTracking()
                .Where(i => i.ContainerId == container.Id)
                .Select(i => new { i.ParticipationId, i.ChallengeId })
                .FirstOrDefaultAsync(token);
            return key is null ? null : (key.ParticipationId, key.ChallengeId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[ActivityLog] Failed to resolve instance key for container {Id}",
                container.ShortId);
            return null;
        }
    }

    public async Task<bool> ValidateContainer(Guid guid, CancellationToken token = default) =>
        await Context.Containers.AnyAsync(c => c.Id == guid, token);

    public async Task<bool> DestroyContainer(Container container,
        ContainerDestroyReason reason = ContainerDestroyReason.User, CancellationToken token = default)
    {
        // Capture the owning game instance key before the row is removed (destroy nulls the link), so the
        // activity log can attribute the destruction afterwards. Best-effort; never affects the destroy.
        var instanceKey = await ResolveInstanceKey(container, token);

        try
        {
            await trafficRegistry.ArchiveAsync(container.Id);

            await service.DestroyContainerAsync(container, token);

            if (container.Status != ContainerStatus.Destroyed)
                return false;

            await cache.RemoveAsync(CacheKey.ConnectionCount(container.Id), token);

            Context.Containers.Remove(container);
            await SaveAsync(token);

            if (instanceKey is not null)
                activityLogger.ContainerDestroyed(instanceKey.Value.ParticipationId,
                    instanceKey.Value.ChallengeId, reason);

            return true;
        }
        catch (Exception ex)
        {
            logger.SystemLog(
                StaticLocalizer[nameof(Resources.Program.ContainerRepository_ContainerDestroyFailed),
                    container.LogId,
                    container.Image.Split("/").LastOrDefault() ?? "", ex.Message],
                TaskStatus.Failed, LogLevel.Warning);
            return false;
        }
    }
}
