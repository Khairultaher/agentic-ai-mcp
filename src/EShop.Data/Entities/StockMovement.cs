namespace EShop.Data.Entities;

public class StockMovement
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public MovementType MovementType { get; set; }
    public int Quantity { get; set; }
    public DateTime MovedOn { get; set; }

    public Product? Product { get; set; }
}
