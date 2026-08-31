using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ReminderBot.Models
{
    public class Reminder
    {
        [Key]
        public int Id { get; set; }

        public string Title { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string ScheduleType { get; set; } = string.Empty;

        public int? IntervalHours { get; set; }
        public int? IntervalMinutes { get; set; }
        public string? WeekDays { get; set; }
        public string Time { get; set; } = "00:00";

        public DateTime? NextTriggerUtc { get; set; }
        public bool IsActive { get; set; } = true;

        // Порядковый номер внутри пользователя (1, 2, 3...)
        public int DisplayOrder { get; set; }

        // Предварительные уведомления (минуты до события через запятую, напр. "1440,60,15")
        public string? PreNotificationOffsets { get; set; }

        // Индекс следующего предварительного уведомления (0-based), -1 = ждём основное событие
        public int NextPreNotificationIndex { get; set; } = 0;

        [ForeignKey(nameof(AppUser))]
        public long UserId { get; set; }
        public AppUser? User { get; set; }
    }
}