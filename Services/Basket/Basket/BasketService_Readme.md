# Basket Service — Explained (Interview Style)

## 1. The one-line pitch

> "The Basket Service manages each user's **shopping cart**. It's an **ASP.NET Core 9 Web API** that stores carts in **Redis**. While items are added, it calls the **Discount service over gRPC** to apply coupons. At checkout, it **publishes an event to RabbitMQ through MassTransit**, so the Ordering service can create the order **asynchronously**. Like Catalog, it uses **CQRS with MediatR** and the **Repository pattern**."

The three big talking points that make this service interesting:
1. **Redis**: why a cache is used as the database
2. **gRPC**: synchronous service-to-service call to Discount
3. **RabbitMQ / MassTransit**: asynchronous, event-driven checkout

---

## 2. Where it sits in the system

```
Angular Client
     │  (localhost:8010/Basket)
     ▼
Ocelot API Gateway  ── adds x-correlation-id header; Checkout requires a JWT
     │
     ▼
Basket API (.NET 9, port 8001)
     │            │                       │
     │ Redis      │ gRPC (sync)           │ RabbitMQ (async)
     ▼            ▼                       ▼
 basketdb     Discount Service       BasketCheckoutEvent
 (6379)       (Postgres coupons)          │
                                          ▼
                                   Ordering Service
                                   (BasketOrderingConsumer)
```

So Basket is the **connector service**: it talks to one service synchronously (Discount) and one asynchronously (Ordering).

---

## 3. Folder structure

| Folder | Contents | Role |
|---|---|---|
| `Entities/` | `ShoppingCart`, `ShoppingCartItem`, `BasketCheckout` | Data stored in Redis |
| `Controllers/` | `BasketController` | HTTP entry point |
| `Commands/` | `CreateShoppingCartCommand`, `DeleteBasketByUserNameCommand`, `CheckoutBasketCommand` | Write operations |
| `Queries/` | `GetBasketByUserNameQuery` | Read operation |
| `Handlers/` | One handler per command or query | Business logic |
| `Repositories/` | `IBasketRepository`, `BasketRepository` | The only code that talks to Redis |
| `GrpcService/` + `Protos/` | `DiscountGrpcService`, `discount.proto` | gRPC client for Discount |
| `DTOs/`, `Responses/`, `Mappers/` | API shapes + converters | Same idea as Catalog |

There's also a shared project, **`EventBus.Messages`**, which holds the event contracts (`BasketCheckoutEvent`, etc.) and queue names that Basket and Ordering both reference.

---

## 4. The data model

```csharp
public class ShoppingCart
{
    public string UserName { get; set; }
    public List<ShoppingCartItem> Items { get; set; } = new();
}

public class ShoppingCartItem
{
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public string ProductId { get; set; }     // the Catalog product's ObjectId
    public string ImageFile { get; set; }
    public string ProductName { get; set; }
}
```

- **One cart per user**, identified by **`UserName`**, which is also the Redis key.
- Items keep a **copy** of product name, price and image, so the cart can be displayed without calling Catalog. This is the same embedding idea as in Catalog.
- **`TotalPrice` is never stored.** It's calculated on the fly in `ShoppingCartResponse`:
  ```csharp
  public decimal TotalPrice => Items.Sum(item => item.Price * item.Quantity);
  ```
  > "A derived value should be computed, not stored, so it can never go out of sync."

---

## 5. Why Redis? (an interviewer will ask)

> "A cart is temporary, read and written very often, and always looked up by a single key: the username. That's exactly what a key-value store is for. Redis keeps data in memory, so reads and writes are sub-millisecond. We don't need joins or complex queries. And if a cart is ever lost, it's an inconvenience, not a business disaster, unlike losing an order."

**How it's stored**: `BasketRepository` uses .NET's **`IDistributedCache`** abstraction:

```csharp
// Save (upsert): serialize the whole cart to JSON, store under key = userName
await _redisCache.SetStringAsync(cart.UserName, JsonConvert.SerializeObject(cart));

// Read
var json = await _redisCache.GetStringAsync(userName);
return JsonConvert.DeserializeObject<ShoppingCart>(json);

// Delete
await _redisCache.RemoveAsync(userName);
```

In Redis it looks like:
```
KEY:   "john"
VALUE: {"UserName":"john","Items":[{"ProductId":"65a1..","ProductName":"Adidas Quick Force","Price":140,"Quantity":2,...}]}
```

Points worth mentioning:
- **Upsert:** there's no separate "add item" endpoint. The client sends the **whole cart** each time, and the service **overwrites** it. That keeps the API very simple.
- **`IDistributedCache`** is an abstraction, so Redis could be swapped for SQL Server or an in-memory cache with a one-line change in `Program.cs`.
- Registered with `AddStackExchangeRedisCache(...)`, which reads the connection string from `CacheSettings:ConnectionString` (`basketdb:6379` in Docker).

---

## 6. API endpoints

Base route: `api/v1/Basket`

| Method | Route | What it does | Returns |
|---|---|---|---|
| GET | `{userName}` | Get the user's cart (an empty cart if none exists) | `ShoppingCartResponse` |
| POST | `/` | Create or replace the cart, applying discounts | updated cart |
| DELETE | `{userName}` | Delete the cart | 200 OK |
| POST | `Checkout` | Start checkout: publish event, clear cart | **202 Accepted** |

Two nice details to point out:
- **GET never returns 404.** If there's no cart, `GetBasketByUserNameHandler` returns an **empty cart**, so the frontend never has to handle "no basket yet" as an error.
- **Checkout returns `202 Accepted`, not `200 OK`.** That's the correct HTTP status for "I've received your request and it will be processed **later**", because the order is created asynchronously by another service.

---

## 7. Flow 1: Add to cart, with a gRPC discount call

`POST /api/v1/Basket` → `CreateShoppingCartCommandHandler`:

```
1. For EACH item in the cart:
       call Discount service via gRPC: GetDiscount(productName)
       item.Price -= coupon.Amount
2. Map command → ShoppingCart entity
3. Save to Redis (upsert)
4. Return the updated cart
```

```csharp
foreach (var item in request.Items) {
    var coupon = await _discountGrpcService.GetDiscount(item.ProductName);
    item.Price -= coupon.Amount;
}
```

If a product has no coupon, the Discount service returns a "No Discount" coupon with `Amount = 0`, so the price stays the same.

### What is gRPC and why use it here?

> "gRPC is a high-performance RPC framework from Google. It runs over **HTTP/2** and sends data as **Protocol Buffers**, a compact binary format, instead of JSON. You define the contract in a `.proto` file, and tooling **generates strongly-typed client and server code**."

The contract (`Protos/discount.proto`):
```proto
service DiscountProtoService {
    rpc GetDiscount(GetDiscountRequest) returns (CouponModel);
    ...
}
message GetDiscountRequest { string productName = 1; }
message CouponModel { int32 id = 1; string productName = 2; string description = 3; int32 amount = 4; }
```

In `Basket.csproj`:
```xml
<Protobuf Include="Protos\discount.proto" GrpcServices="Client" />
```
`GrpcServices="Client"` means: **generate only the client code**. Basket calls Discount; it doesn't serve gRPC itself. The Discount service has the same `.proto` with `GrpcServices="Server"`.

Wiring in `Program.cs`:
```csharp
builder.Services.AddGrpcClient<DiscountProtoService.DiscountProtoServiceClient>(
    cfg => cfg.Address = new Uri(builder.Configuration["GrpcSettings:DiscountUrl"]));
builder.Services.AddScoped<DiscountGrpcService>();   // a small wrapper around the generated client
```

**Why gRPC instead of REST for this call?**
> "This is **internal** service-to-service traffic. The browser never calls Discount, so we don't need JSON readability. gRPC is faster (binary payload, HTTP/2 connection reuse), and the `.proto` file is a **strict shared contract**: if Discount changes the contract, Basket fails at compile time instead of at runtime."

**Why is this call synchronous?**
> "We need the discount **right now**, to show the correct price in the cart. The user is waiting for the answer, so a request-response call fits."

---

## 8. Where does `GetDiscountAsync` come from? (generated gRPC client code)

`DiscountGrpcService.GetDiscount` calls:
```csharp
var discountRequest = new GetDiscountRequest { ProductName = productName };
return await _discountProtoServiceClient.GetDiscountAsync(discountRequest);
```

You won't find `GetDiscountAsync` in any file you wrote. The **gRPC tooling generates it automatically** from `discount.proto` when the project builds.

### Where it physically is

```
Services/Basket/Basket/obj/Debug/net9.0/Protos/
├── Discount.cs        ← message classes (GetDiscountRequest, CouponModel, …)
└── DiscountGrpc.cs    ← service/client classes  ← GetDiscountAsync is here
```

In `DiscountGrpc.cs`:
```csharp
public partial class DiscountProtoServiceClient : grpc::ClientBase<DiscountProtoServiceClient>
{
    ...
    public virtual grpc::AsyncUnaryCall<CouponModel> GetDiscountAsync(
        GetDiscountRequest request,
        grpc::Metadata headers = null,
        DateTime? deadline = null,
        CancellationToken cancellationToken = default)
    {
        return GetDiscountAsync(request, new grpc::CallOptions(headers, deadline, cancellationToken));
    }

    public virtual grpc::AsyncUnaryCall<CouponModel> GetDiscountAsync(GetDiscountRequest request, grpc::CallOptions options)
    {
        return CallInvoker.AsyncUnaryCall(__Method_GetDiscount, null, options, request);
    }
}
```

The `obj` folder is hidden in Solution Explorer by default. The quickest way to jump there in Visual Studio is to put the cursor on `GetDiscountAsync` and press **F12** (Go To Definition).

### How it gets generated

1. You define the RPC in `Protos/discount.proto`: `rpc GetDiscount(GetDiscountRequest) returns (CouponModel);`
2. `Basket.csproj` tells the build to generate client code: `<Protobuf Include="Protos\discount.proto" GrpcServices="Client" />`. The `Grpc.Tools` NuGet package provides the Protobuf compiler (`protoc`) and the C# gRPC plugin.
3. On every build, `protoc` reads the `.proto` file and writes C# into `obj/`:

| In the `.proto` | Generated C# |
|---|---|
| `service DiscountProtoService` | `static partial class DiscountProtoService` |
| (client side) | nested class `DiscountProtoServiceClient` |
| `rpc GetDiscount(...)` | **4 methods**: `GetDiscount` (blocking, 2 overloads) and **`GetDiscountAsync`** (async, 2 overloads) |
| `message GetDiscountRequest` | class `GetDiscountRequest` (in `Discount.cs`) |
| `message CouponModel` | class `CouponModel` (in `Discount.cs`) |
| `option csharp_namespace = "Discount.Grpc.Protos"` | the namespace, which is why you write `using Discount.Grpc.Protos;` |

The plugin **adds the `Async` suffix by itself**. Every `rpc` gets a synchronous version and an `...Async` version.

### What happens when you call it

1. `GetDiscountAsync` hands the request to `CallInvoker.AsyncUnaryCall(__Method_GetDiscount, ...)`.
2. `__Method_GetDiscount` describes the call: it's **unary** (one request, one response), its name is `"GetDiscount"`, and it lists the **marshallers** that convert objects to and from Protobuf bytes.
3. The request is serialized to binary and sent over **HTTP/2** to `POST /DiscountProtoService/GetDiscount` at the address configured by `AddGrpcClient` in `Program.cs`. `AddGrpcClient` also registers the generated client in DI, which is how `DiscountGrpcService` receives it in its constructor.
4. On the Discount side, the matching generated **server** base class routes the call to the override `DiscountService.GetDiscount(...)`.
5. The response bytes are deserialized back into a `CouponModel`.

It returns `AsyncUnaryCall<CouponModel>`, not `Task<CouponModel>`. That type is still **awaitable**, so `await` gives you the `CouponModel` directly. It also exposes extras like `.ResponseHeadersAsync` and `.GetStatus()`.

### Why this matters

- **Don't edit files in `obj/`.** They're regenerated on every build, so your changes would be lost.
- **To add or change a gRPC method, edit the `.proto` and rebuild.** The client methods appear automatically.
- **If `GetDiscountAsync` shows a red squiggle,** the project usually just hasn't been built yet. Rebuild it.
- **The Discount project generates the opposite side** from its own copy of the `.proto` (`GrpcServices="Server"`): an abstract `DiscountProtoServiceBase` with a `virtual GetDiscount(...)` method, which `DiscountService.cs` **overrides**.

**Interview line:**
> "I never write the gRPC client by hand. The `.proto` file is the contract, and `Grpc.Tools` generates a strongly-typed client at build time. Basket generates the client stub and Discount generates the server base class from the same contract. Calling `GetDiscountAsync` looks like a local method call, but it actually serializes the request to Protobuf and sends it over HTTP/2."

---

## 9. Flow 2: Checkout, event-driven with RabbitMQ (the most important part)

`POST /api/v1/Basket/Checkout` → `CheckoutBasketCommandHandler`:

```
1. Load the cart (by sending GetBasketByUserNameQuery through MediatR)
2. If the cart is empty → throw "Basket not found or empty"
3. Build a BasketCheckoutEvent:
      - shipping + payment details from the request DTO
      - TotalPrice CALCULATED ON THE SERVER from the cart items
4. Copy the x-correlation-id header into the event's CorrelationId
5. Publish the event to RabbitMQ (MassTransit IPublishEndpoint)
6. Delete the cart from Redis
7. Return 202 Accepted
```

The event (shared contract in `EventBus.Messages`):
```csharp
public class BaseIntegrationEvent
{
    public Guid CorrelationId { get; set; }
    public DateTime CreationDate { get; set; }   // set to UtcNow
}
public class BasketCheckoutEvent : BaseIntegrationEvent
{
    public string? UserName, FirstName, LastName, EmailAddress, AddressLine, ...;
    public decimal? TotalPrice;
    public int? PaymentMethod;
}
```

On the other side, **Ordering** has a `BasketOrderingConsumer : IConsumer<BasketCheckoutEvent>` listening on the `basketcheckout-queue`. It turns the event into an order.

### Why asynchronous messaging for checkout?

> "Checkout starts a longer business process: create the order, then process payment. If Basket called Ordering directly over HTTP and Ordering was down, checkout would fail. With RabbitMQ, Basket **publishes the event and moves on**. The message waits safely in the queue until Ordering is ready. That gives us:
> - **Loose coupling:** Basket doesn't even know who consumes the event.
> - **Resilience:** Ordering can be down or slow and no orders are lost.
> - **Scalability:** we can run several Ordering instances that share the queue.
> - **Extensibility:** a new service (say, Email notifications) can subscribe to the same event without changing Basket."

### Why MassTransit instead of using RabbitMQ directly?

> "MassTransit is an abstraction over message brokers. It handles connections, serialization, exchanges, queues, retries and error queues for me. I just call `Publish(evt)`. And if we moved to Azure Service Bus, only the configuration would change, not the business code."

```csharp
builder.Services.AddMassTransit(config =>
{
    config.UsingRabbitMq((ct, cfg) =>
        cfg.Host(builder.Configuration["EventBusSettings:HostAddress"]));  // amqp://guest:guest@rabbitmq:5672
});
```

### Correlation ID (distributed tracing)
- The **API Gateway** adds an `x-correlation-id` header to every Basket request (configured in `ocelot.json`).
- The handler copies it into `evt.CorrelationId`, so the ID **travels inside the message** to Ordering, and then on to Payment.
- All services log through Serilog to **Elasticsearch**, so you can search one correlation ID and see the **whole journey of a single checkout across services**.

> "In microservices, one user action touches several services. Without a correlation ID, the logs are scattered pieces. With one, you can trace a whole request end-to-end."

### Security detail: the total price is calculated on the server
The DTO includes a `TotalPrice` from the client, but the mapper **ignores it** and recalculates:
```csharp
TotalPrice = basket.Items.Sum(item => item.Price * item.Quantity),
```
> "Never trust the client with money. A user could edit the request and send `TotalPrice: 1`."

---

## 10. MediatR inside a handler

`CheckoutBasketCommandHandler` uses **MediatR itself** to reuse other handlers:
```csharp
var basket = await _mediator.Send(new GetBasketByUserNameQuery(dto.UserName));
...
await _mediator.Send(new DeleteBasketByUserNameCommand(dto.UserName));
```
> "Instead of duplicating 'get basket' and 'delete basket' logic, the checkout handler reuses the existing query and command. Each piece of logic lives in exactly one place."

(Some teams avoid handlers calling other handlers, because it can hide dependencies. Calling the repository directly is the alternative. Either approach is defensible.)

---

## 11. Configuration and Docker

`appsettings.json` (local) vs `docker-compose.override.yml` (containers):

| Setting | Local | Docker |
|---|---|---|
| `CacheSettings:ConnectionString` | `localhost:6379` | `basketdb:6379` |
| `GrpcSettings:DiscountUrl` | `http://localhost:8002` | `http://discount:8080` |
| `EventBusSettings:HostAddress` | `amqp://guest:guest@localhost:5672` | `amqp://guest:guest@rabbitmq:5672/` |

- `depends_on: basketdb, rabbitmq` starts those containers first. (That only controls start order; it doesn't wait until they're actually ready.)
- The service is exposed on port **8001**, with the same multi-stage Dockerfile and Serilog + `CorrelationIdMiddleware` setup as Catalog.
- **Gateway security:** in `ocelot.json`, only the **Checkout** route has `AuthenticationOptions: Bearer`. Browsing and editing the cart works anonymously, but you must be logged in to check out. That matches how real shops behave.

---

## 12. "What would you improve?"

1. **Likely bug: discounts are applied again on every cart update.** The Angular client stores the cart returned by the server, which already has discounted prices, and posts the **whole cart** back on the next change. The handler subtracts the coupon again each time, so the price keeps dropping. *Fix:* store the original price and the discount separately (`Price` + `DiscountedPrice`), or always look up the base price from Catalog.
2. **Prices come from the client.** A user could send `Price: 0.01` when adding to the cart. Checkout recalculates the total, but from those client-supplied item prices. *Fix:* fetch the real price from Catalog (or keep a server-side price cache) when upserting.
3. **Publish and delete aren't atomic.** If publishing succeeds but deleting the cart fails, or the other way round, the system ends up in an inconsistent state. *Fix:* the **Outbox pattern** (MassTransit supports it), or idempotent consumers in Ordering.
4. **Card number and CVV travel through RabbitMQ** in plain text and may end up in logs or queues. Real systems never do this: they send a **payment token** from a provider like Stripe. PCI-DSS rules forbid storing CVVs at all.
5. **No expiration on carts.** Abandoned carts stay in Redis forever. *Fix:* set `AbsoluteExpirationRelativeToNow` or `SlidingExpiration` via `DistributedCacheEntryOptions`.
6. **No resilience on the gRPC call.** If Discount is down, users can't add anything to their cart. *Fix:* catch `RpcException` and continue without the discount, and/or add retry and timeout policies (Polly).
7. **N+1 gRPC calls.** One Discount call per item, made one after another. *Fix:* a batch `GetDiscounts(productNames[])` RPC, or `Task.WhenAll` for parallel calls.
8. **The cart endpoints aren't protected.** Anyone can `GET` or `DELETE /Basket/someoneElse`. The username should come from the JWT, not the URL.
9. **No global exception handling.** "Basket empty" throws `InvalidOperationException`, which becomes a 500 instead of a 400.
10. **`ToEntity(ShoppingCartResponse)` drops `ImageFile`.** It's harmless today because only checkout uses it, but it's a trap for future use.
11. **Minor items:**
    - `DELETE` returns `200 OK`; `204 No Content` is more conventional.
    - `UpsertBasket` makes an extra Redis read after writing; it could just return the object it saved.
    - The service mixes **Newtonsoft.Json** with the built-in **System.Text.Json**.

---

## 13. A 60-second version to memorize

> "Basket is an ASP.NET Core 9 microservice that manages shopping carts. A cart is temporary, frequently accessed and always looked up by username, so I store it in Redis as a JSON string keyed by username, using `IDistributedCache`. The API is simple: get, upsert the whole cart, delete, and checkout. When a cart is saved, the handler calls the Discount service over gRPC. That's internal traffic, so I chose binary Protobuf over HTTP/2 with a strongly-typed contract from a `.proto` file. Checkout is event-driven: the handler validates the cart, calculates the total on the server so the client can't tamper with it, publishes a `BasketCheckoutEvent` to RabbitMQ through MassTransit, deletes the cart, and returns 202 Accepted. The Ordering service consumes that event independently, so Basket doesn't depend on Ordering being up. A correlation ID from the gateway travels inside the event, so we can trace one checkout across all services in Elasticsearch. The code uses CQRS with MediatR and a repository, like Catalog. If I kept working on it, I'd fix the repeated-discount issue, take prices from Catalog instead of the client, add the Outbox pattern, use payment tokens instead of card data, add cart expiry, and make the gRPC call resilient."
