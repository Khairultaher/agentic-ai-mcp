namespace EShop.Data.Entities;

public class Customer
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
    public int ZoneId { get; set; }
    public DateTime RegisteredOn { get; set; }

    public Zone? Zone { get; set; }
    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
