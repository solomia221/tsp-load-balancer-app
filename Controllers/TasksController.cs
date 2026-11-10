using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TSP.Backend.Data;
using TSP.Backend.Models;
using TSP.Backend.Services;

namespace TSP.Backend.Controllers;

[Authorize]
[Route("api/[controller]")]
[ApiController]
public class TasksController : ControllerBase
{
    
    public const int MinCities = 3, MaxCities = 20;
    public const int MaxGenerations = 1000;
    public const int MinPopulation = 10, MaxPopulation = 200;
    public const int MaxActivePerUser = 5;
    public const int MaxQueueLength = 100; 

    private readonly AppDbContext _context;
    private readonly QueueEstimator _queue;

    public TasksController(AppDbContext context, QueueEstimator queue)
    {
        _context = context;
        _queue = queue;
    }

    private Guid GetCurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static string? Validate(CreateTaskRequest r)
    {
        var m = r.DistanceMatrix;
        if (m == null || m.Length == 0) return "Матриця відстаней не може бути порожньою.";
        if (m.Length < MinCities || m.Length > MaxCities) return $"Кількість міст: від {MinCities} до {MaxCities}.";
        if (m.Any(row => row == null || row.Length != m.Length)) return "Матриця відстаней має бути квадратною.";
        if (m.Any(row => row.Any(v => double.IsNaN(v) || double.IsInfinity(v) || v < 0)))
            return "Матриця містить некоректні значення.";
        if (r.Generations < 1 || r.Generations > MaxGenerations) return $"Кількість ітерацій: від 1 до {MaxGenerations}.";
        if (r.PopulationSize < MinPopulation || r.PopulationSize > MaxPopulation)
            return $"Розмір популяції: від {MinPopulation} до {MaxPopulation}.";
        return null;
    }

    [HttpPost]
    public async Task<IActionResult> CreateTask([FromBody] CreateTaskRequest request)
    {
        var error = Validate(request);
        if (error != null) return BadRequest(new { message = error });

        var userId = GetCurrentUserId();

        var activeCount = await _context.ComputeTasks.CountAsync(t => t.UserId == userId &&
            (t.Status == TaskStatusEnum.Pending || t.Status == TaskStatusEnum.Processing));
        if (activeCount >= MaxActivePerUser)
            return BadRequest(new { message = $"Перевищено ліміт: максимум {MaxActivePerUser} активних задач одночасно." });

        var queued = await _context.ComputeTasks.CountAsync(t => t.Status == TaskStatusEnum.Pending);
        if (queued >= MaxQueueLength)
            return StatusCode(503, new { message = "Система перевантажена: черга заповнена. Спробуйте пізніше." });

        var task = new ComputeTask
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Status = TaskStatusEnum.Pending,
            Progress = 0,
            
            InputData = JsonSerializer.Serialize(new TspInput(request.DistanceMatrix, request.Generations, request.PopulationSize)),
            CreatedAt = DateTime.UtcNow
        };
        _context.ComputeTasks.Add(task);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetTaskById), new { id = task.Id }, new { taskId = task.Id, status = task.Status.ToString() });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetTaskById(Guid id)
    {
        var userId = GetCurrentUserId();
        var task = await _context.ComputeTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);
        if (task == null) return NotFound(new { message = "Задача не знайдена або доступ заборонено." });

        List<int>? route = null;
        string? error = null;
        if (!string.IsNullOrEmpty(task.ResultData))
        {
            if (task.Status == TaskStatusEnum.Completed)
            {
                try { route = JsonSerializer.Deserialize<List<int>>(task.ResultData); } catch { }
            }
            else if (task.Status == TaskStatusEnum.Failed)
            {
                try { error = JsonSerializer.Deserialize<string>(task.ResultData); } catch { error = task.ResultData; }
            }
        }

        int? position = null, eta = null;
        if (task.Status == TaskStatusEnum.Pending)
        {
            var q = await _queue.EstimateAsync(task);
            position = q.Position; eta = q.EtaSeconds;
        }

        return Ok(new
        {
            task.Id,
            Status = task.Status.ToString(),
            task.Progress,
            task.CreatedAt,
            task.StartedAt,
            task.CompletedAt,
            task.AssignedServerId,
            QueuePosition = position,
            EstimatedWaitSeconds = eta,
            ResultRoute = route,
            Error = error
        });
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> CancelTask(Guid id)
    {
        var userId = GetCurrentUserId();
        // Атомарно: скасовуємо лише якщо задача ще активна (воркер не встигне її "завершити" одночасно)
        var changed = await _context.ComputeTasks
            .Where(t => t.Id == id && t.UserId == userId &&
                        (t.Status == TaskStatusEnum.Pending || t.Status == TaskStatusEnum.Processing))
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.Status, TaskStatusEnum.Canceled)
                .SetProperty(t => t.CompletedAt, (DateTime?)DateTime.UtcNow));

        if (changed > 0) return Ok(new { message = "Задачу скасовано." });

        var exists = await _context.ComputeTasks.AnyAsync(t => t.Id == id && t.UserId == userId);
        return exists
            ? BadRequest(new { message = "Неможливо скасувати завершену задачу." })
            : NotFound(new { message = "Задача не знайдена або доступ заборонено." });
    }

    [HttpGet]
    public async Task<IActionResult> GetAllTasks()
    {
        var userId = GetCurrentUserId();
        var rows = await _context.ComputeTasks.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(200)
            .Select(t => new { t.Id, t.Status, t.Progress, t.CreatedAt, t.CompletedAt, t.AssignedServerId })
            .ToListAsync();

        return Ok(rows.Select(t => new
        {
            t.Id,
            Status = t.Status.ToString(),
            t.Progress,
            t.CreatedAt,
            t.CompletedAt,
            t.AssignedServerId
        }));
    }
}

public class CreateTaskRequest
{
    public double[][] DistanceMatrix { get; set; } = Array.Empty<double[]>();
    public int Generations { get; set; } = 100;
    public int PopulationSize { get; set; } = 50;
}