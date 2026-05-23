namespace EShop.Data.Entities;

public class Zone
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Region { get; set; }

    public ICollection<Customer> Customers { get; set; } = new List<Customer>();
    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
