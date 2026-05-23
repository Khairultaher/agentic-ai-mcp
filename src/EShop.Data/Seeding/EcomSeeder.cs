using EShop.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace EShop.Data.Seeding;

public static class EcomSeeder
{
    private const int RngSeed = 42;
    private const int TargetCustomers = 500;
    private const int TargetOrders = 3000;
    private const int OrderWindowDays = 365;
    private const int CustomerWindowDays = 540; // 18 months

    public static async Task<SeedResult> SeedAsync(EcomDbContext db, CancellationToken ct = default)
    {
        if (await db.Zones.AnyAsync(ct))
        {
            return SeedResult.AlreadySeeded;
        }

        var rng = new Random(RngSeed);
        var today = DateTime.UtcNow.Date;

        var zones = CreateZones();
        var categories = CreateCategories();
        await db.Zones.AddRangeAsync(zones, ct);
        await db.Categories.AddRangeAsync(categories, ct);
        await db.SaveChangesAsync(ct);

        var products = CreateProducts(categories, rng);
        await db.Products.AddRangeAsync(products, ct);

        var customers = CreateCustomers(zones, rng, today);
        await db.Customers.AddRangeAsync(customers, ct);
        await db.SaveChangesAsync(ct);

        var zoneBias = zones.ToDictionary(
            z => z.Id,
            z => Math.Round((rng.NextDouble() - 0.3) * 0.12, 4)); // ~[-0.036, +0.084] revenue bias per zone

        var (orders, items, movements) = CreateOrdersGraph(products, customers, zoneBias, rng, today);
        await db.Orders.AddRangeAsync(orders, ct);
        await db.OrderItems.AddRangeAsync(items, ct);
        await db.StockMovements.AddRangeAsync(movements, ct);
        await db.SaveChangesAsync(ct);

        return new SeedResult(
            ZonesAdded: zones.Count,
            CategoriesAdded: categories.Count,
            ProductsAdded: products.Count,
            CustomersAdded: customers.Count,
            OrdersAdded: orders.Count,
            OrderItemsAdded: items.Count,
            StockMovementsAdded: movements.Count);
    }

    private static List<Zone> CreateZones() =>
    [
        new() { Name = "North-1",   Region = "North"  },
        new() { Name = "North-2",   Region = "North"  },
        new() { Name = "North-3",   Region = "North"  },
        new() { Name = "Central-1", Region = "Central"},
        new() { Name = "Central-2", Region = "Central"},
        new() { Name = "Central-3", Region = "Central"},
        new() { Name = "South-1",   Region = "South"  },
        new() { Name = "South-2",   Region = "South"  },
    ];

    private static List<Category> CreateCategories() =>
    [
        new() { Name = "Electronics" },
        new() { Name = "Apparel"     },
        new() { Name = "Home"        },
        new() { Name = "Sports"      },
        new() { Name = "Beauty"      },
        new() { Name = "Books"       },
    ];

    private static List<Product> CreateProducts(List<Category> categories, Random rng)
    {
        // (categoryName, basePriceLow, basePriceHigh, marginLow, marginHigh, namePool)
        var blueprints = new (string CategoryName, decimal Low, decimal High, double MarginLow, double MarginHigh, string[] Names)[]
        {
            ("Electronics", 80m, 1200m, 0.18, 0.32, new[]
            {
                "Wireless Earbuds", "Bluetooth Speaker", "4K Action Cam", "Smart Watch",
                "Mech Keyboard", "Gaming Mouse", "USB-C Hub", "Portable SSD",
                "Noise Cancelling Headphones", "Phone Stand"
            }),
            ("Apparel", 12m, 180m, 0.40, 0.60, new[]
            {
                "Cotton Tee", "Slim Jeans", "Hoodie", "Running Shorts",
                "Wool Beanie", "Leather Belt", "Linen Shirt", "Puffer Jacket"
            }),
            ("Home", 8m, 220m, 0.30, 0.55, new[]
            {
                "Ceramic Mug", "Bamboo Cutting Board", "LED Desk Lamp",
                "Throw Blanket", "Air Purifier", "Espresso Cups Set",
                "Storage Bins (3-pk)", "Memory Foam Pillow"
            }),
            ("Sports", 15m, 350m, 0.25, 0.45, new[]
            {
                "Yoga Mat", "Resistance Band Set", "Foam Roller",
                "Trail Running Shoes", "Insulated Bottle", "Bike Helmet",
                "Dumbbell Pair", "Training Gloves"
            }),
            ("Beauty", 6m, 80m, 0.45, 0.70, new[]
            {
                "Vitamin C Serum", "Hydrating Toner", "Beard Oil",
                "Mineral Sunscreen", "Hair Mask", "Lip Balm Trio",
                "Clay Mask", "Eye Cream"
            }),
            ("Books", 8m, 45m, 0.20, 0.40, new[]
            {
                "Distributed Systems Primer", "Mindful Productivity",
                "The Art of Indexing", "Modern Microservices",
                "Cooking from Scratch", "Watercolor Basics"
            }),
        };

        var products = new List<Product>();
        foreach (var bp in blueprints)
        {
            var category = categories.Single(c => c.Name == bp.CategoryName);
            foreach (var name in bp.Names)
            {
                var price = Math.Round((decimal)(rng.NextDouble() * (double)(bp.High - bp.Low)) + bp.Low, 2);
                var margin = bp.MarginLow + rng.NextDouble() * (bp.MarginHigh - bp.MarginLow);
                var cost = Math.Round(price * (decimal)(1.0 - margin), 2);
                products.Add(new Product
                {
                    Name = name,
                    Category = category,
                    Price = price,
                    Cost = cost,
                    StockQty = rng.Next(200, 1500),
                });
            }
        }
        return products;
    }

    private static List<Customer> CreateCustomers(List<Zone> zones, Random rng, DateTime today)
    {
        string[] firstNames = ["Aarav","Bilal","Chen","Dipa","Elif","Farah","Gopal","Hana","Imran","Juno","Kamal","Lina","Maya","Noah","Olu","Priya","Quan","Reza","Sami","Tia","Umar","Vega","Wren","Xena","Yara","Zane"];
        string[] lastNames  = ["Acharya","Begum","Choudhury","Das","Ekram","Faisal","Ghosh","Habib","Iqbal","Jamal","Khan","Latif","Mahmud","Nasrin","Osman","Patel","Quader","Rahman","Saha","Talukder","Uddin","Vora","Wadud","Xu","Yusuf","Zaman"];

        var customers = new List<Customer>(TargetCustomers);
        for (var i = 0; i < TargetCustomers; i++)
        {
            var first = firstNames[rng.Next(firstNames.Length)];
            var last = lastNames[rng.Next(lastNames.Length)];
            var zone = zones[rng.Next(zones.Count)];
            var registered = today.AddDays(-rng.Next(0, CustomerWindowDays))
                                  .AddMinutes(rng.Next(0, 60 * 24));
            customers.Add(new Customer
            {
                Name = $"{first} {last}",
                Email = $"{first}.{last}.{i:D4}@example.com".ToLowerInvariant(),
                Zone = zone,
                RegisteredOn = registered,
            });
        }
        return customers;
    }

    private static (List<Order> Orders, List<OrderItem> Items, List<StockMovement> Movements) CreateOrdersGraph(
        List<Product> products,
        List<Customer> customers,
        Dictionary<int, double> zoneBiasById,
        Random rng,
        DateTime today)
    {
        var orders = new List<Order>(TargetOrders);
        var items = new List<OrderItem>(TargetOrders * 3);
        var movements = new List<StockMovement>(TargetOrders * 3);

        var statuses = new[]
        {
            (OrderStatus.Delivered, 0.55),
            (OrderStatus.Shipped,   0.20),
            (OrderStatus.Paid,      0.15),
            (OrderStatus.Pending,   0.06),
            (OrderStatus.Cancelled, 0.03),
            (OrderStatus.Refunded,  0.01),
        };

        var generated = 0;
        var safety = TargetOrders * 5;
        while (generated < TargetOrders && safety-- > 0)
        {
            var daysAgo = rng.Next(0, OrderWindowDays);
            var day = today.AddDays(-daysAgo);
            var weekendBump = day.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday or DayOfWeek.Sunday;
            // Rejection sampling: Mon-Thu kept 50% of the time, Fri-Sun always kept.
            if (!weekendBump && rng.NextDouble() < 0.5)
            {
                continue;
            }

            var orderDate = day.AddHours(rng.Next(8, 22)).AddMinutes(rng.Next(0, 60));
            var customer = customers[rng.Next(customers.Count)];
            var zoneId = customer.ZoneId != 0 ? customer.ZoneId : customer.Zone!.Id;
            var bias = zoneBiasById[zoneId];

            var itemCount = WeightedPick(rng, [(1, 0.30), (2, 0.35), (3, 0.20), (4, 0.10), (5, 0.05)]);
            var orderItems = new List<OrderItem>(itemCount);
            var orderMovements = new List<StockMovement>(itemCount);
            var total = 0m;
            var usedProductIds = new HashSet<int>();

            for (var i = 0; i < itemCount; i++)
            {
                Product product;
                var attempts = 0;
                do
                {
                    product = products[rng.Next(products.Count)];
                    attempts++;
                } while (!usedProductIds.Add(product.Id) && attempts < 5);

                var qty = WeightedPick(rng, [(1, 0.55), (2, 0.30), (3, 0.10), (4, 0.05)]);
                var unitPrice = Math.Round(product.Price * (decimal)(1 + bias), 2);
                var unitCost = product.Cost;

                orderItems.Add(new OrderItem
                {
                    ProductId = product.Id,
                    Quantity = qty,
                    UnitPrice = unitPrice,
                    UnitCost = unitCost,
                });
                orderMovements.Add(new StockMovement
                {
                    ProductId = product.Id,
                    MovementType = MovementType.Out,
                    Quantity = qty,
                    MovedOn = orderDate,
                });
                total += unitPrice * qty;
            }

            var status = WeightedPick(rng, statuses);
            var order = new Order
            {
                CustomerId = customer.Id,
                ZoneId = zoneId,
                OrderDate = orderDate,
                Status = status,
                TotalAmount = total,
                Items = orderItems,
            };

            foreach (var oi in orderItems)
            {
                oi.Order = order;
            }

            orders.Add(order);
            items.AddRange(orderItems);
            movements.AddRange(orderMovements);
            generated++;
        }

        return (orders, items, movements);
    }

    private static T WeightedPick<T>(Random rng, (T Value, double Weight)[] options)
    {
        var total = options.Sum(o => o.Weight);
        var roll = rng.NextDouble() * total;
        var acc = 0.0;
        foreach (var (value, weight) in options)
        {
            acc += weight;
            if (roll <= acc) return value;
        }
        return options[^1].Value;
    }
}

public readonly record struct SeedResult(
    int ZonesAdded,
    int CategoriesAdded,
    int ProductsAdded,
    int CustomersAdded,
    int OrdersAdded,
    int OrderItemsAdded,
    int StockMovementsAdded)
{
    public static readonly SeedResult AlreadySeeded = new(0, 0, 0, 0, 0, 0, 0);

    public bool WasSeeded => OrdersAdded > 0 || CustomersAdded > 0;
}
