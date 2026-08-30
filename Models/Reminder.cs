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
        public string? WeekDays { get; set; }
        public string Time { get; set; } = "00:00";
        public DateTime? NextTriggerUtc { get; set; }
        public bool IsActive { get; set; } = true;
        
        [ForeignKey(nameof(AppUser))]
        public long UserId { get; set; }
        public AppUser? User { get; set; }
    }
}