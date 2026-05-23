namespace EShop.Data.Entities;

public class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public int ZoneId { get; set; }
    public DateTime OrderDate { get; set; }
    public OrderStatus Status { get; set; }
    public decimal TotalAmount { get; set; }

    public Customer? Customer { get; set; }
    public Zone? Zone { get; set; }
    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
}
