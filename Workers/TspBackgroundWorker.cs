using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TSP.Backend.Data;
using TSP.Backend.Models;
using TSP.Backend.Services;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace TSP.Backend.Workers
{
    public class TspBackgroundWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public TspBackgroundWorker(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var tspService = scope.ServiceProvider.GetRequiredService<TspSolverService>();

                    
                    var task = dbContext.ComputeTasks
                        .FirstOrDefault(t => t.Status == TaskStatusEnum.Pending);

                    if (task != null)
                    {
                        
                        task.Status = TaskStatusEnum.Processing;
                        task.Progress = 0;
                        task.StartedAt = DateTime.UtcNow;
                        await dbContext.SaveChangesAsync(stoppingToken);

                        try
                        {
                            
                            double[,] distanceMatrix = JsonSerializer.Deserialize<double[,]>(task.InputData!)
                                ?? new double[,] { { 0, 1 }, { 1, 0 } };

                            // оновлення прогресу (0-100%)
                            var bestRoute = tspService.SolveTsp(
                                distanceMatrix,
                                generations: 100,
                                populationSize: 50,
                                mutationRate: 0.1,
                                reportProgress: (percent) =>
                                {
                                    task.Progress = percent;
                                    dbContext.SaveChanges();
                                }
                            );

                            
                            task.ResultData = JsonSerializer.Serialize(bestRoute);
                            task.Status = TaskStatusEnum.Completed;
                            task.Progress = 100;
                            task.CompletedAt = DateTime.UtcNow;
                        }
                        catch (Exception ex)
                        {
                            task.Status = TaskStatusEnum.Failed;
                            task.ResultData = ex.Message;
                            task.CompletedAt = DateTime.UtcNow;
                        }

                        await dbContext.SaveChangesAsync(stoppingToken);
                    }
                }

                
                await Task.Delay(3000, stoppingToken);
            }
        }
    }
}