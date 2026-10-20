using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TSP.Backend.Models
{
    public enum TaskStatusEnum
    {
        Pending = 0,
        Processing = 1,
        Completed = 2,
        Failed = 3,
        Canceled = 4
    }

    public class ComputeTask
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        public TaskStatusEnum Status { get; set; } = TaskStatusEnum.Pending;

        [Range(0, 100)]
        public int Progress { get; set; } = 0; // Відсоток виконання

        // Використовуємо jsonb для гнучкого збереження масивів координат
        [Required]
        [Column(TypeName = "jsonb")]
        public string? InputData { get; set; }

        [Column(TypeName = "jsonb")]
        public string? ResultData { get; set; }

        public string? AssignedServerId { get; set; } // ID воркера, щоб інші сервери не перехопили задачу

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }
}