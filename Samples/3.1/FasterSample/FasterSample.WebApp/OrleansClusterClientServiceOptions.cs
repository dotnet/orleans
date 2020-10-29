using System.ComponentModel.DataAnnotations;

namespace FasterSample.WebApp
{
    public class OrleansClusterClientServiceOptions
    {
        [Required]
        public int ConnectionAttempts { get; set; } = 10;
    }
}