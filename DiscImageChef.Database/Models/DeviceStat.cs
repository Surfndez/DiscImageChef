using System.ComponentModel.DataAnnotations;

namespace DiscImageChef.Database.Models
{
    public class DeviceStat
    {
        [Key]
        public int Id { get;              set; }
        public string Manufacturer { get; set; }
        public string Model        { get; set; }
        public string Revision     { get; set; }
        public string Bus          { get; set; }
        public bool   Synchronized { get; set; }
    }
}