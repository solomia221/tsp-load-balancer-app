using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using TSP.Backend.Data;
using TSP.Backend.Models;

namespace TSP.Backend.Controllers
{
    [Authorize] 
    [Route("api/[controller]")]
    [ApiController]
    public class TasksController : ControllerBase
    {
        private readonly AppDbContext _context;

        public TasksController(AppDbContext context)
        {
            _context = context;
        }

        private Guid GetCurrentUserId()
        {
            return Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        }

        [HttpPost]
        public async Task<IActionResult> CreateTask([FromBody] CreateTaskRequest request)
        {
            if (request.DistanceMatrix == null || request.DistanceMatrix.Length == 0)
                return BadRequest(new { message = "Матриця відстаней не може бути порожньою." });

            if (request.DistanceMatrix.Length > 20 || request.Generations > 1000)
                return BadRequest(new { message = "Перевищено ліміт: макс. 20 міст та 1000 ітерацій." });

            var userId = GetCurrentUserId();

            
            var activeTasksCount = await _context.ComputeTasks
                .CountAsync(t => t.UserId == userId &&
                                 (t.Status == TaskStatusEnum.Pending || t.Status == TaskStatusEnum.Processing));

            if (activeTasksCount >= 5)
                return BadRequest(new { message = "Перевищено ліміт: максимум 5 активних задач одночасно. Дочекайтесь їх завершення." });

            
            var task = new ComputeTask
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Status = TaskStatusEnum.Pending,
                Progress = 0,
                InputData = JsonSerializer.Serialize(request.DistanceMatrix),
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
            var task = await _context.ComputeTasks.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);

            if (task == null) return NotFound(new { message = "Задача не знайдена або доступ заборонено." });

            List<int>? resultRoute = null;
            if (!string.IsNullOrEmpty(task.ResultData) && task.Status == TaskStatusEnum.Completed)
            {
                try { resultRoute = JsonSerializer.Deserialize<List<int>>(task.ResultData); } catch { }
            }

            return Ok(new
            {
                task.Id,
                Status = task.Status.ToString(),
                task.Progress,
                task.CreatedAt,
                task.StartedAt,
                task.CompletedAt,
                ResultRoute = resultRoute,
                Error = task.Status == TaskStatusEnum.Failed ? task.ResultData : null
            });
        }

        [HttpPost("{id:guid}/cancel")]
        public async Task<IActionResult> CancelTask(Guid id)
        {
            var userId = GetCurrentUserId();
            var task = await _context.ComputeTasks.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);

            if (task == null) return NotFound(new { message = "Задача не знайдена або доступ заборонено." });
            if (task.Status == TaskStatusEnum.Completed || task.Status == TaskStatusEnum.Failed)
                return BadRequest(new { message = "Неможливо скасувати завершену задачу." });

            task.Status = TaskStatusEnum.Canceled;
            task.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Задача успішно скасована." });
        }

        [HttpGet]
        public async Task<IActionResult> GetAllTasks()
        {
            var userId = GetCurrentUserId();
            var tasks = await _context.ComputeTasks
                .Where(t => t.UserId == userId) 
                .OrderByDescending(t => t.CreatedAt)
                .Select(t => new { t.Id, Status = t.Status.ToString(), t.Progress, t.CreatedAt, t.CompletedAt })
                .ToListAsync();

            return Ok(tasks);
        }
    }

    public class CreateTaskRequest
    {
        public double[][] DistanceMatrix { get; set; } = Array.Empty<double[]>();
        public int Generations { get; set; } = 100;
        public int PopulationSize { get; set; } = 50;
    }
}