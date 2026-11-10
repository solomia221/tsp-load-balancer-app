using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TSP.Backend.Data;
using TSP.Backend.Models;
using TSP.Backend.Services;

namespace TSP.Backend.Workers;

public class TspBackgroundWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly NodeIdentity _node;
    private readonly ILogger<TspBackgroundWorker> _log;
    private readonly TimeSpan _timeout;
    private readonly int _delayMs;
    private readonly ConcurrentDictionary<Guid, Task> _active = new();

    public TspBackgroundWorker(IServiceScopeFactory scopes, NodeIdentity node, IConfiguration cfg, ILogger<TspBackgroundWorker> log)
    {
        _scopes = scopes; _node = node; _log = log;
        _timeout = TimeSpan.FromSeconds(cfg.GetValue("Worker:TaskTimeoutSeconds", 120));
        _delayMs = cfg.GetValue("Worker:SimulatedDelayMsPerGeneration", 0);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await RequeueOwnAsync();

        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (_active.Count < _node.MaxConcurrent)
                {
                    var id = await ClaimAsync(stop);
                    if (id != null)
                    {
                        _active[id.Value] = Task.Run(() => RunAsync(id.Value, stop), CancellationToken.None);
                        continue;
                    }
                }
                await Task.Delay(1000, stop);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                _log.LogError(e, "Помилка циклу воркера");
                try { await Task.Delay(2000, stop); } catch (OperationCanceledException) { break; }
            }
        }
        await Task.WhenAny(Task.WhenAll(_active.Values), Task.Delay(8000));
    }

    private async Task RequeueOwnAsync()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.ComputeTasks.Where(t => t.Status == TaskStatusEnum.Processing && t.AssignedServerId == _node.Id)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(t => t.Status, TaskStatusEnum.Pending)
                    .SetProperty(t => t.Progress, 0)
                    .SetProperty(t => t.StartedAt, (DateTime?)null)
                    .SetProperty(t => t.AssignedServerId, (string?)null));
        }
        catch (Exception e) { _log.LogWarning(e, "Не вдалося повернути в чергу задачі після перезапуску"); }
    }

    private async Task<Guid?> ClaimAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var claimed = await db.ComputeTasks.FromSqlRaw("""
            UPDATE "ComputeTasks"
            SET "Status" = 1, "Progress" = 0, "StartedAt" = NOW(), "AssignedServerId" = {0}
            WHERE "Id" = (SELECT "Id" FROM "ComputeTasks" WHERE "Status" = 0
                          ORDER BY "CreatedAt" LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING *
            """, _node.Id).AsNoTracking().ToListAsync(ct);
        return claimed.FirstOrDefault()?.Id;
    }

    private async Task RunAsync(Guid id, CancellationToken stop)
    {
        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var cancelCts = CancellationTokenSource.CreateLinkedTokenSource(stop, timeoutCts.Token);
        using var watcherDone = new CancellationTokenSource();
        var latest = 0;
        var userCanceled = false;
        Task? watcher = null;

        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var solver = scope.ServiceProvider.GetRequiredService<TspSolverService>();

            var task = await db.ComputeTasks.AsNoTracking().FirstAsync(t => t.Id == id, CancellationToken.None);
            var input = JsonSerializer.Deserialize<TspInput>(task.InputData!)!;

            watcher = Task.Run(async () =>
            {
                while (!watcherDone.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(1000, watcherDone.Token);
                        using var s = _scopes.CreateScope();
                        var d = s.ServiceProvider.GetRequiredService<AppDbContext>();
                        var p = Volatile.Read(ref latest);
                        await d.ComputeTasks.Where(t => t.Id == id && t.Status == TaskStatusEnum.Processing)
                            .ExecuteUpdateAsync(u => u.SetProperty(t => t.Progress, p));
                        var status = await d.ComputeTasks.Where(t => t.Id == id).Select(t => t.Status).FirstAsync();
                        if (status == TaskStatusEnum.Canceled) { userCanceled = true; cancelCts.Cancel(); }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e) { _log.LogWarning(e, "Watcher задачі {Id}", id); }
                }
            });

            var size = input.DistanceMatrix.Length;
            var matrix = new double[size, size];
            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++)
                    matrix[i, j] = input.DistanceMatrix[i][j];

            var route = await Task.Run(() => solver.SolveTsp(
                matrix,
                generations: input.Generations,
                populationSize: input.PopulationSize,
                mutationRate: 0.1,
                reportProgress: p => Volatile.Write(ref latest, p),
                ct: cancelCts.Token,
                delayMsPerGeneration: _delayMs), CancellationToken.None);

            var jsonResult = JsonSerializer.Serialize(route);

            using var s2 = _scopes.CreateScope();
            var d2 = s2.ServiceProvider.GetRequiredService<AppDbContext>();
            await d2.ComputeTasks.Where(t => t.Id == id && t.Status == TaskStatusEnum.Processing)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(t => t.Status, TaskStatusEnum.Completed)
                    .SetProperty(t => t.Progress, 100)
                    .SetProperty(t => t.ResultData, jsonResult)
                    .SetProperty(t => t.CompletedAt, (DateTime?)DateTime.UtcNow));
        }
        catch (OperationCanceledException)
        {
            if (stop.IsCancellationRequested) await RequeueAsync(id);
            else if (timeoutCts.IsCancellationRequested)
                await FailAsync(id, $"Перевищено ліміт часу виконання ({_timeout.TotalSeconds:0} с).");
            else if (!userCanceled) _log.LogWarning("Задачу {Id} перервано з невідомої причини", id);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Задача {Id} завершилась помилкою", id);
            await FailAsync(id, "Внутрішня помилка обчислення.");
        }
        finally
        {
            watcherDone.Cancel();
            if (watcher != null) try { await watcher; } catch { }
            _active.TryRemove(id, out _);
        }
    }

    private async Task FailAsync(Guid id, string message)
    {
        var jsonMessage = JsonSerializer.Serialize(message);

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.ComputeTasks.Where(t => t.Id == id && t.Status == TaskStatusEnum.Processing)
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.Status, TaskStatusEnum.Failed)
                .SetProperty(t => t.ResultData, jsonMessage)
                .SetProperty(t => t.CompletedAt, (DateTime?)DateTime.UtcNow));
    }

    private async Task RequeueAsync(Guid id)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.ComputeTasks.Where(t => t.Id == id && t.Status == TaskStatusEnum.Processing)
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.Status, TaskStatusEnum.Pending)
                .SetProperty(t => t.Progress, 0)
                .SetProperty(t => t.StartedAt, (DateTime?)null)
                .SetProperty(t => t.AssignedServerId, (string?)null));
    }
}