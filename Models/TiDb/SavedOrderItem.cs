using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace CostFlow.Models.TiDb
{
    public class SavedOrderItem
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        public int BatchId { get; set; }

        [ForeignKey("BatchId")]
        [JsonIgnore]
        public SavedOrderBatch Batch { get; set; } = null!;

        [MaxLength(100)]
        public string ProductCode { get; set; } = string.Empty;

        [MaxLength(255)]
        public string ProductName { get; set; } = string.Empty;

        [MaxLength(50)]
        public string Unit { get; set; } = string.Empty;

        public decimal UnitPrice { get; set; }
        public decimal Quantity { get; set; }

        public string Remarks { get; set; } = string.Empty;

        public bool IsReceived { get; set; } = false;
        public DateTime? ReceiveDate { get; set; }
    }
}
