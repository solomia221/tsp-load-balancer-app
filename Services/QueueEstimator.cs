using Microsoft.EntityFrameworkCore;
using TSP.Backend.Data;
using TSP.Backend.Models;

namespace TSP.Backend.Services;


public class NodeIdentity
{
    public string Id { get; }                 
    public int MaxConcurrent { get; }         
    public string[] Servers { get; }          

    public NodeIdentity(IConfiguration cfg)
    {
        Id = cfg["ServerId"] ?? Environment.MachineName;
        MaxConcurrent = Math.Max(1, cfg.GetValue("Worker:MaxConcurrentTasks", 2));
        Servers = (cfg["Cluster:Servers"] ?? Id)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

public record QueueInfo(int Position, int EtaSeconds);


public class QueueEstimator
{
    private const double DefaultTaskSeconds = 20;
    private readonly AppDbContext _db;
    private readonly NodeIdentity _node;

    public QueueEstimator(AppDbContext db, NodeIdentity node)
    {
        _db = db;
        _node = node;
    }

    public async Task<QueueInfo> EstimateAsync(ComputeTask task)
    {
        
        var capacity = _node.Servers.Length * _node.MaxConcurrent;
        var running = await _db.ComputeTasks.CountAsync(t => t.Status == TaskStatusEnum.Processing);
        var position = await _db.ComputeTasks.CountAsync(t => t.Status == TaskStatusEnum.Pending && t.CreatedAt <= task.CreatedAt);

        
        var durations = await _db.ComputeTasks
            .Where(t => t.Status == TaskStatusEnum.Completed && t.StartedAt != null && t.CompletedAt != null)
            .OrderByDescending(t => t.CompletedAt).Take(20)
            .Select(t => new { t.StartedAt, t.CompletedAt }).ToListAsync();
        var avg = durations.Count > 0
            ? durations.Average(d => (d.CompletedAt!.Value - d.StartedAt!.Value).TotalSeconds)
            : DefaultTaskSeconds;

        
        var free = Math.Max(0, capacity - running);
        if (position <= free) return new QueueInfo(position, 0);
        var waves = (int)Math.Ceiling((position - free) / (double)capacity);
        return new QueueInfo(position, (int)Math.Round((waves - 0.5) * avg));
    }
}