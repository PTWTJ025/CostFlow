using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CostFlow.Models.TiDb
{
    public class SavedOrderBatch
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        [MaxLength(255)]
        public string BatchName { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        public int TotalItems { get; set; }
        public decimal TotalAmount { get; set; }

        public ICollection<SavedOrderItem> Items { get; set; } = new List<SavedOrderItem>();
    }
}
