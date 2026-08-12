using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Configurar Swagger y CORS
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

// Registrar cliente de MongoDB leyendo variable de entorno
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
app.UseCors("AllowAll");

// --- ENDPOINTS DE LA API ---

// P1, P3, P4: Generar Orden de Compra
app.MapPost("/api/orders", async (
    [FromBody] CreateOrderRequest request,
    [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
    IMongoDatabase db,
    IHttpClientFactory clientFactory) =>
{
    var ordersCollection = db.GetCollection<Order>("Orders");

    // 1. Validar Idempotencia (P4)
    if (!string.IsNullOrEmpty(idempotencyKey))
    {
        var existingOrder = await ordersCollection.Find(o => o.IdempotencyKey == idempotencyKey).FirstOrDefaultAsync();
        if (existingOrder != null)
        {
            return Results.Ok(existingOrder);
        }
    }

    // 2. Obtener Basket desde el Microservicio de Basket en producción
    var client = clientFactory.CreateClient();
    var basketUrl = $"https://basket-api-cma3.onrender.com/basket/{request.CustomerId}";

    HttpResponseMessage response;
    try
    {
        response = await client.GetAsync(basketUrl);
    }
    catch
    {
        return Results.Problem("Error al conectar con el microservicio de Basket.", statusCode: 500);
    }

    if (!response.IsSuccessStatusCode)
    {
        return Results.BadRequest(new { error = "No se pudo obtener el basket del cliente." });
    }

    var basket = await response.Content.ReadFromJsonAsync<BasketDto>();

    // 3. Validar Basket Vacío (P3)
    if (basket == null || basket.Items == null || !basket.Items.Any())
    {
        return Results.BadRequest(new { error = "El Basket está vacío. No se puede generar la orden." });
    }

    // 4. Calcular Totales y Crear la Orden
    var orderItems = basket.Items.Select(item => new OrderItem
    {
        ProductId = item.ProductId,
        ProductName = item.ProductName,
        Quantity = item.Quantity,
        UnitPrice = item.UnitPrice,
        LineTotal = item.UnitPrice * item.Quantity
    }).ToList();

    var subtotal = orderItems.Sum(i => i.LineTotal);
    var tax = subtotal * 0.16m; // 16% IVA
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

    // 5. Persistir en MongoDB Atlas (P1)
    await ordersCollection.InsertOneAsync(order);

    return Results.Created($"/api/orders/{order.Id}", order);
});

// P2: Consultar Orden por ID
app.MapGet("/api/orders/{id}", async (string id, IMongoDatabase db) =>
{
    var ordersCollection = db.GetCollection<Order>("Orders");
    var order = await ordersCollection.Find(o => o.Id == id).FirstOrDefaultAsync();
    return order != null ? Results.Ok(order) : Results.NotFound(new { error = "Orden no encontrada." });
});

// Consultar Órdenes por Cliente
app.MapGet("/api/orders/customer/{customerId}", async (string customerId, IMongoDatabase db) =>
{
    var ordersCollection = db.GetCollection<Order>("Orders");
    var orders = await ordersCollection.Find(o => o.CustomerId == customerId).ToListAsync();
    return Results.Ok(orders);
});

// P5, P6: Cambiar Estado de Orden (Transiciones de estado)
app.MapPatch("/api/orders/{id}/status", async (string id, [FromBody] UpdateStatusRequest req, IMongoDatabase db) =>
{
    var ordersCollection = db.GetCollection<Order>("Orders");
    var order = await ordersCollection.Find(o => o.Id == id).FirstOrDefaultAsync();

    if (order == null) return Results.NotFound(new { error = "Orden no encontrada." });

    // Validar reglas de transición: Pending -> Confirmed, Pending -> Cancelled
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
});

app.Run();

// --- MODELOS Y DTOS (SIEMPRE AL FINAL DEL ARCHIVO EN MINIMAL API) ---
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

public record CreateOrderRequest(string CustomerId, string BasketId);
public record UpdateStatusRequest(string Status);
public record BasketDto(string BuyerId, List<BasketItemDto> Items);
public record BasketItemDto(string ProductId, string ProductName, decimal UnitPrice, int Quantity);