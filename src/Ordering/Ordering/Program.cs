using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 1. Definir la política CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", p => p
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var mongoConnectionString = builder.Configuration["ConnectionStrings:MongoDb"]
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__MongoDb")
    ?? "mongodb://localhost:27017";

var mongoClient = new MongoClient(mongoConnectionString);
var database = mongoClient.GetDatabase("OrderingDb");

builder.Services.AddSingleton(database);
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// 2. Colocar UseCors INMEDIATAMENTE antes del enrutamiento
app.UseRouting();
app.UseCors("AllowAll");

// 3. INTERCEPTOR GLOBAL DE OPTIONS: Responde 200 OK a cualquier Preflight inmediatamente
app.MapMethods("{*path}", new[] { "OPTIONS" }, () => Results.Ok())
   .RequireCors("AllowAll");

// POST /api/orders
app.MapPost("/api/orders", async (
    [FromBody] CreateOrderRequest request,
    [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
    IMongoDatabase db,
    IHttpClientFactory clientFactory,
    IConfiguration config) =>
{
    var ordersCollection = db.GetCollection<Order>("Orders");

    if (!string.IsNullOrEmpty(idempotencyKey))
    {
        var existingOrder = await ordersCollection.Find(o => o.IdempotencyKey == idempotencyKey).FirstOrDefaultAsync();
        if (existingOrder != null) return Results.Ok(existingOrder);
    }

    List<OrderItem> orderItems = new();

    if (request.Items != null && request.Items.Any())
    {
        orderItems = request.Items.Select(i => new OrderItem
        {
            ProductId = string.IsNullOrEmpty(i.ProductId) ? "prod-1" : i.ProductId,
            ProductName = string.IsNullOrEmpty(i.ProductName) ? "Producto" : i.ProductName,
            Quantity = i.Quantity > 0 ? i.Quantity : 1,
            UnitPrice = i.UnitPrice > 0 ? i.UnitPrice : i.Price,
            LineTotal = (i.UnitPrice > 0 ? i.UnitPrice : i.Price) * (i.Quantity > 0 ? i.Quantity : 1)
        }).ToList();
    }
    else
    {
        try
        {
            var client = clientFactory.CreateClient();
            var baseUrl = config["BasketApiUrl"] ?? "https://basket-api-cma3.onrender.com";
            var basketUrl = $"{baseUrl.TrimEnd('/')}/api/basket/{request.CustomerId}";

            var response = await client.GetAsync(basketUrl);
            if (response.IsSuccessStatusCode)
            {
                var basketResponse = await response.Content.ReadFromJsonAsync<BasketResponseDto>();
                var basket = basketResponse?.Cart ?? basketResponse;
                if (basket?.Items != null && basket.Items.Any())
                {
                    orderItems = basket.Items.Select(item => {
                        var price = item.UnitPrice > 0 ? item.UnitPrice : item.Price;
                        return new OrderItem
                        {
                            ProductId = item.ProductId ?? "prod-1",
                            ProductName = item.ProductName ?? "Producto",
                            Quantity = item.Quantity,
                            UnitPrice = price,
                            LineTotal = price * item.Quantity
                        };
                    }).ToList();
                }
            }
        }
        catch { }
    }

    if (!orderItems.Any())
    {
        return Results.BadRequest(new { error = "El Basket está vacío. No se puede generar la orden." });
    }

    var subtotal = orderItems.Sum(i => i.LineTotal);
    var tax = subtotal * 0.16m;
    var total = subtotal + tax;

    var order = new Order
    {
        CustomerId = request.CustomerId,
        IdempotencyKey = idempotencyKey ?? Guid.NewGuid().ToString(),
        CreatedAt = DateTime.UtcNow,
        Status = "Pending",
        Items = orderItems,
        Subtotal = subtotal,
        Tax = tax,
        Total = total
    };

    await ordersCollection.InsertOneAsync(order);
    return Results.Created($"/api/orders/{order.Id}", order);
}).RequireCors("AllowAll");

app.MapGet("/api/orders/{id}", async (string id, IMongoDatabase db) =>
{
    var ordersCollection = db.GetCollection<Order>("Orders");
    var order = await ordersCollection.Find(o => o.Id == id).FirstOrDefaultAsync();
    return order != null ? Results.Ok(order) : Results.NotFound(new { error = "Orden no encontrada." });
}).RequireCors("AllowAll");

app.MapGet("/api/orders/customer/{customerId}", async (string customerId, IMongoDatabase db) =>
{
    var ordersCollection = db.GetCollection<Order>("Orders");
    var orders = await ordersCollection.Find(o => o.CustomerId == customerId).ToListAsync();
    return Results.Ok(orders);
}).RequireCors("AllowAll");

app.MapPatch("/api/orders/{id}/status", async (string id, [FromBody] UpdateStatusRequest req, IMongoDatabase db) =>
{
    var ordersCollection = db.GetCollection<Order>("Orders");
    var order = await ordersCollection.Find(o => o.Id == id).FirstOrDefaultAsync();

    if (order == null) return Results.NotFound(new { error = "Orden no encontrada." });

    if (order.Status == "Cancelled")
    {
        return Results.BadRequest(new { error = "Una orden Cancelada no puede cambiar de estado." });
    }

    if (order.Status == "Confirmed" && req.Status == "Pending")
    {
        return Results.BadRequest(new { error = "Transición de estado no permitida." });
    }

    var update = Builders<Order>.Update.Set(o => o.Status, req.Status);
    await ordersCollection.UpdateOneAsync(o => o.Id == id, update);

    order.Status = req.Status;
    return Results.Ok(order);
}).RequireCors("AllowAll");

app.Run();

// DTOs y Clases
public class Order
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Pending";
    public List<OrderItem> Items { get; set; } = new();
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
}

public class OrderItem
{
    public string ProductId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
}

public record CreateOrderRequest(string CustomerId, string BasketId, List<OrderItemInputDto>? Items = null);
public record OrderItemInputDto(string? ProductId, string? ProductName, decimal UnitPrice, decimal Price, int Quantity);
public record UpdateStatusRequest(string Status);

public class BasketResponseDto
{
    public string? BuyerId { get; set; }
    public string? UserName { get; set; }
    public List<BasketItemDto> Items { get; set; } = new();
    public BasketResponseDto? Cart { get; set; }
}

public class BasketItemDto
{
    public string? ProductId { get; set; }
    public string? ProductName { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Price { get; set; }
    public int Quantity { get; set; }
}