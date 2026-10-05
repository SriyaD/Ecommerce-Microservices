# Ordering Service — Explained (Interview Style)

## 1. The one-line pitch

> "The Ordering Service turns a checked-out basket into a **permanent order** and tracks it through payment. It's an **ASP.NET Core 9 Web API** on **SQL Server with Entity Framework Core**. It is mainly **event-driven**: it **consumes** `BasketCheckoutEvent` from RabbitMQ, saves the order, and uses the **Transactional Outbox pattern** to publish an `OrderCreatedEvent` reliably to the Payment service. It then listens for `PaymentCompleted` and `PaymentFailed` events to update the order's status. Inside, it uses **CQRS with MediatR**, **FluentValidation through MediatR pipeline behaviors**, and a **generic repository**."

This is the most "enterprise" of the services. The big talking points:
1. **Event consumer + event publisher**: the middle of the checkout chain
2. **Transactional Outbox pattern** + a **BackgroundService** dispatcher
3. **Choreography-based saga**: the Pending → Paid/Failed order lifecycle
4. **EF Core**: migrations, audit fields, design-time factory, enum-to-string conversion
5. **MediatR pipeline behaviors**: validation and exception logging as cross-cutting concerns
6. **Polly** retries at startup

---

## 2. Where it sits: the full checkout flow

```
 Basket ──BasketCheckoutEvent──► RabbitMQ [basketcheckout-queue]
                                       │
                                       ▼
                         ┌──── Ordering Service ─────────────────────────┐
                         │ BasketOrderingConsumer                        │
                         │   → CheckoutOrderCommand → handler            │
                         │   → INSERT Order (Status = Pending)           │
                         │   → INSERT OutboxMessage (OrderCreatedEvent)  │
                         │                                               │
                         │ OutboxMessageDispatcher (every 5s)            │
                         │   → reads unprocessed OutboxMessages          │
                         │   → Publish(OrderCreatedEvent)                │
                         └──────────────────┬────────────────────────────┘
                                            ▼
                               RabbitMQ [ordercreated-queue]
                                            ▼
                                   Payment Service
                                 (OrderCreatedConsumer)
                                     │            │
                    PaymentCompletedEvent    PaymentFailedEvent
                                     ▼            ▼
                         Ordering: PaymentCompletedConsumer → Status = Paid
                                   PaymentFailedConsumer    → Status = Failed
```

> "No service calls another directly. Each one reacts to an event and publishes the next one. That's a **choreography-based saga**: a business process that spans several services, coordinated only through events, with no central controller."

**Why SQL Server and EF Core for orders?**
> "Orders are the **system of record**: money is involved, so I want **ACID transactions**, a strict schema and relational integrity. EF Core gives me change tracking, migrations and LINQ. For a service with real domain logic, that productivity is worth it, unlike Discount, where Dapper was enough."

---

## 3. Folder structure

| Folder | Contents | Role |
|---|---|---|
| `Controllers/` | `OrderController` | REST API (get, create, update, delete) |
| `EventBusConsumer/` | `BasketOrderingConsumer`, `PaymentCompletedConsumer`, `PaymentFailedConsumer` | **RabbitMQ message handlers** |
| `Commands/`, `Queries/` | `CheckoutOrderCommand`, `UpdateOrderCommand`, `DeleteOrderCommand`, `GetOrderList` | CQRS messages |
| `Handlers/` | One per command or query | Business logic |
| `Behaviour.cs/` | `ValidationBehaviour`, `UnhandledExceptionBehaviour` | **MediatR pipeline** middleware |
| `Validators/` | `CheckoutOrderCommandValidator`, `UpdateOrderCommandValidator` | FluentValidation rules |
| `Data/` | `OrderContext`, `OrderContextFactory`, `OrderContextSeed` | EF Core |
| `Migrations/` | `InitialCreate`, `AddedOutBoxTable` | Database schema history |
| `Entities/` | `EntityBase`, `Order`, `OrderStatus`, `OutboxMessage` | Domain + tables |
| `Dispatcher/` | `OutboxMessageDispatcher` | **Background worker** that publishes outbox messages |
| `Repositories/` | `IAsyncRepository<T>`, `RepositoryBase<T>`, `IOrderRepository`, `OrderRepository` | Generic + specific repositories |
| `Extensions/` | `ApplicationServices`, `InfraServices`, `DbExtension` | Clean DI registration + migrations |
| `Mappers/`, `DTOs/`, `Exceptions/`, `Constants/` | Supporting code | |

`Program.cs` stays short because registration is grouped into **extension methods**: `AddApplicationServices()` (MediatR, validators, behaviors) and `AddInfraServices()` (DbContext, repositories). That's a light **Clean Architecture** style split between the application and infrastructure layers.

---

## 4. The data model

```csharp
public abstract class EntityBase
{
    public int Id { get; protected set; }        // only EF or derived classes can set it
    public string? CreatedBy { get; set; }       // audit fields
    public DateTime? CreatedDate { get; set; }
    public string? LastModifiedBy { get; set; }
    public DateTime? LastModifiedDate { get; set; }
}

public class Order : EntityBase
{
    public string? UserName, FirstName, LastName, EmailAddress, AddressLine, Country, State, ZipCode;
    public string? CardName, CardNumber, Expiration, Cvv;
    public decimal? TotalPrice;
    public int? PaymentMethod;
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
}

public enum OrderStatus { Pending = 0, Paid = 1, Failed = 2 }
```

Points to mention:
- **`Id { get; protected set; }`**: outside code can't change an order's ID. SQL Server generates it (IDENTITY) and EF fills it in after insert.
- **Status as a string in the database:**
  ```csharp
  modelBuilder.Entity<Order>().Property(o => o.Status).HasConversion<string>();
  ```
  > "The column stores `'Paid'` instead of `1`. That's readable in SQL queries, and it's safe if someone reorders the enum values later."
- **Automatic audit fields:** `OrderContext` overrides `SaveChangesAsync`:
  ```csharp
  foreach (var entry in ChangeTracker.Entries<EntityBase>())
      switch (entry.State) {
          case EntityState.Added:    entry.Entity.CreatedDate = DateTime.UtcNow; entry.Entity.CreatedBy = "Sriya"; break;
          case EntityState.Modified: entry.Entity.LastModifiedDate = DateTime.UtcNow; entry.Entity.LastModifiedBy = "Sriya"; break;
      }
  ```
  > "Every insert or update gets audit stamps automatically, so no handler can forget to set them. `CreatedBy` is hard-coded for now. The TODO is to take it from the logged-in user."

---

## 5. API endpoints

Base route `api/v1/Order`. Through the gateway: `/Order/...` → port 8003.

| Method | Route | What it does |
|---|---|---|
| GET | `{userName}` | A user's order history ("My Orders" page) |
| POST | `/` | Create an order directly (comment says "testing purpose"; the real path is the event) |
| PUT | `/` | Update an order |
| DELETE | `{id}` | Delete an order |

Every write endpoint reads `x-correlation-id` from the header, or generates a new one, and puts it on the command so it ends up in the outbox message.

**Key point:** in normal use, **orders are not created through the REST API**. They're created by the **RabbitMQ consumer**. The REST API is mostly for reading order history and for admin or testing.

---

## 6. Flow 1: Consuming the checkout event

```csharp
public class BasketOrderingConsumer : IConsumer<BasketCheckoutEvent>
{
    public async Task Consume(ConsumeContext<BasketCheckoutEvent> context)
    {
        using var scope = _logger.BeginScope("Consuming Basket Checkout Event for {CorrelationId}", context.Message.CorrelationId);
        var command = context.Message.ToCheckoutOrderCommand();
        var result = await _mediator.Send(command);
    }
}
```

Wiring in `Program.cs`:
```csharp
builder.Services.AddMassTransit(config =>
{
    config.AddConsumer<BasketOrderingConsumer>();
    config.AddConsumer<PaymentCompletedConsumer>();
    config.AddConsumer<PaymentFailedConsumer>();

    config.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["EventBusSettings:HostAddress"]);
        cfg.ReceiveEndpoint(EventBusConstant.BasketCheckoutQueue,     c => c.ConfigureConsumer<BasketOrderingConsumer>(ctx));
        cfg.ReceiveEndpoint(EventBusConstant.PaymentCompletedQueue,   e => e.ConfigureConsumer<PaymentCompletedConsumer>(ctx));
        cfg.ReceiveEndpoint(EventBusConstant.PaymentFailedQueue,      e => e.ConfigureConsumer<PaymentFailedConsumer>(ctx));
    });
});
```

How to explain it:
> "Basket **publishes** to an exchange named after the message type. Here Ordering declares a **named queue**, `basketcheckout-queue`, and MassTransit **binds** it to that exchange. Each message is delivered to one consumer instance. If I scale Ordering to 3 instances, they share the queue as **competing consumers**, so each order is processed once.
>
> The consumer is thin, like a controller: it maps the event to a `CheckoutOrderCommand` and sends it through MediatR. So an order created from RabbitMQ and an order created from REST go through **exactly the same handler, validation and logging**."

`BeginScope(...)` adds the correlation ID to **every log line** written while the message is processed. (Note: `ToCheckoutOrderCommand` doesn't copy the `CorrelationId` onto the command, so it doesn't reach the outbox message or the `OrderCreatedEvent`. See improvement #3.)

---

## 7. MediatR pipeline behaviors (validation + logging)

This is something Catalog, Basket and Discount **don't** have:

```csharp
services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblies(Assembly.GetExecutingAssembly()));
services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());
services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehaviour<,>));
services.AddTransient(typeof(IPipelineBehavior<,>), typeof(UnhandledExceptionBehaviour<,>));
```

Every `_mediator.Send(...)` now passes through a pipeline, like ASP.NET middleware but for MediatR:

```
_mediator.Send(command)
   ▼
ValidationBehaviour      → runs all IValidator<TRequest>; throws ValidationException if any fail
   ▼
UnhandledExceptionBehaviour → try { next() } catch { log with request name; rethrow }
   ▼
Actual handler (CheckoutOrderHandler, …)
```

**`ValidationBehaviour`:**
```csharp
var results = await Task.WhenAll(_validators.Select(v => v.ValidateAsync(context, ct)));
var failures = results.SelectMany(r => r.Errors).Where(f => f != null).ToList();
if (failures.Count > 0) throw new ValidationException(failures);
return await next();
```

**`CheckoutOrderCommandValidator`** (FluentValidation):
```csharp
RuleFor(o => o.UserName).NotEmpty().MaximumLength(70);
RuleFor(o => o.TotalPrice).NotNull().GreaterThanOrEqualTo(0);
RuleFor(o => o.EmailAddress).NotEmpty().EmailAddress();
RuleFor(o => o.CardNumber).CreditCard().When(o => !string.IsNullOrEmpty(o.CardNumber));   // Luhn check
RuleFor(o => o.Expiration).Matches(@"^(0[1-9]|1[0-2])\/?([0-9]{2})$");                    // MM/YY
RuleFor(o => o.Cvv).Matches(@"^\d{3,4}$");
RuleFor(o => o.PaymentMethod).NotNull().InclusiveBetween(1, 3);
```

> "Validation and error logging are **cross-cutting concerns**. Instead of copying `if (invalid) throw` into every handler, I wrote them once as **generic** pipeline behaviors. They apply automatically to every command and query. A handler only runs if its input is already valid. Adding validation to a new command just means adding an `AbstractValidator<T>` class, which is auto-discovered by `AddValidatorsFromAssembly`. That's the **Open/Closed principle** in practice."

### Where is `CheckoutOrderCommandValidator` actually used?

Nothing in the code calls `CheckoutOrderCommandValidator` by name. It still runs **automatically on every `CheckoutOrderCommand`**, because three pieces are wired together through **dependency injection and generics**:

**1. Registration** (`Extensions/ApplicationServices.cs`):
```csharp
services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());
```
At startup, FluentValidation **scans the Ordering assembly** for every class that inherits `AbstractValidator<T>` and registers each one in DI under its interface:
```
IValidator<CheckoutOrderCommand> → CheckoutOrderCommandValidator
IValidator<UpdateOrderCommand>   → UpdateOrderCommandValidator
```

**2. The behavior is registered for every request** (`ApplicationServices.cs`):
```csharp
services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehaviour<,>));
```
This **open generic** registration wraps **every** `Send(...)` in a `ValidationBehaviour<TRequest, TResponse>`.

**3. The behavior asks DI for validators of that request type** (`Behaviour.cs/ValidationBehaviour.cs`):
```csharp
public ValidationBehaviour(IEnumerable<IValidator<TRequest>> validators)
```
When the request is a `CheckoutOrderCommand`, DI injects **`IEnumerable<IValidator<CheckoutOrderCommand>>`**, which contains **`CheckoutOrderCommandValidator`**. This is the exact point where the class gets used.

The full runtime flow:
```
BasketOrderingConsumer.Consume(...)        or      OrderController.CheckoutOrder(...)
        │  _mediator.Send(new CheckoutOrderCommand {...})
        ▼
ValidationBehaviour<CheckoutOrderCommand, int>
   │  DI injects IEnumerable<IValidator<CheckoutOrderCommand>>
   │     = [ CheckoutOrderCommandValidator ]          ◄── HERE
   │  runs RuleFor(UserName), RuleFor(EmailAddress), RuleFor(CardNumber)...
   │
   ├── any rule fails → throw ValidationException ──► handler is NOT called
   ▼
UnhandledExceptionBehaviour
   ▼
CheckoutOrderHandler.Handle(...)   ← only reached with valid data
```

- **Requests without a validator** (`GetOrderList`, `DeleteOrderCommand`): DI injects an **empty** list, `_validators.Any()` is false, and the behavior just calls `next()`. That's why the constructor takes an `IEnumerable`.
- **When validation fails:**

| Path | Result |
|---|---|
| **REST** (`POST /api/v1/Order`) | `ValidationException` becomes an **HTTP 500** (should be 400, since there's no global exception handler) |
| **RabbitMQ** (`BasketOrderingConsumer`) | The consumer throws, so MassTransit moves the message to **`basketcheckout-queue_error`**. **No order is created**, and the user isn't notified. You can see those messages in the RabbitMQ UI at `http://localhost:15672`. |

- **How to verify:** put a breakpoint in the `CheckoutOrderCommandValidator` constructor, or `POST /api/v1/Order` from Swagger with `"emailAddress": "abc"`.

> "Validators aren't called explicitly. `AddValidatorsFromAssembly` registers every `AbstractValidator<T>` in DI as `IValidator<T>`. A generic MediatR pipeline behavior takes `IEnumerable<IValidator<TRequest>>` in its constructor, so for a `CheckoutOrderCommand`, DI injects `CheckoutOrderCommandValidator`. The behavior runs all rules before the handler and throws if any fail."

---

## 8. Flow 2: Creating the order + the Transactional Outbox (the key topic)

`CheckoutOrderHandler`:
```csharp
var orderEntity = request.ToEntity();
var generatedOrder = await _orderRepository.AddAsync(orderEntity);                  // INSERT Order
var outboxMessage  = OrderMapper.ToOutboxMessage(generatedOrder, request.CorrelationId);
await _orderRepository.AddOutboxMessageAsync(outboxMessage);                       // INSERT OutboxMessage
return generatedOrder.Id;
```

### The problem the outbox solves (the "dual-write problem")
> "After saving an order, I must tell Payment about it. The naive approach is to save to the database and then publish to RabbitMQ. That's **two different systems**, and they can't share a transaction:
> - Database succeeds, then the app crashes or RabbitMQ is down → **the order exists but Payment never hears about it**. It's stuck in Pending forever.
> - Publish first, then the database fails → **Payment charges for an order that doesn't exist**."

### The solution
> "Instead of publishing directly, I write the event as a **row in an `OutboxMessages` table in the same database**. A separate background worker then reads unprocessed rows and publishes them. If RabbitMQ is down, the row just waits and is retried later. The event can be **delayed, but never lost**."

(For this guarantee to hold, the order and the outbox row must be saved in the **same transaction**. In the current code they're saved by two separate `SaveChangesAsync()` calls. See improvement #1.)

The outbox table:
```csharp
public class OutboxMessage : EntityBase
{
    public string Type { get; set; }          // "OrderCreated"
    public string Content { get; set; }       // the full OrderCreatedEvent as JSON
    public string CorrelationId { get; set; } // indexed, for tracing
    public DateTime OccurredOn { get; set; }
    public DateTime? ProcessedOn { get; set; } // null = not yet published
    public string? Error { get; set; }
}
```

### The dispatcher: `OutboxMessageDispatcher : BackgroundService`
```csharp
while (!stoppingToken.IsCancellationRequested)
{
    using var scope = _serviceProvider.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<OrderContext>();
    var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

    var pending = await dbContext.OutboxMessages
        .Where(x => x.ProcessedOn == null)
        .OrderBy(x => x.OccurredOn)       // keep events in order
        .Take(20)                         // batch size
        .ToListAsync(stoppingToken);

    foreach (var message in pending)
    {
        try {
            var evt = JsonConvert.DeserializeObject<OrderCreatedEvent>(message.Content);
            await publishEndpoint.Publish(evt);
            message.ProcessedOn = DateTime.UtcNow;     // mark as sent
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to publish outbox message {Id}", message.Id); }
    }
    await dbContext.SaveChangesAsync(stoppingToken);
    await Task.Delay(5000, stoppingToken);              // poll every 5 seconds
}
```

Points to explain:
- **`BackgroundService`** is a long-running task hosted inside the web app. It's registered with `AddHostedService<OutboxMessageDispatcher>()`, starts with the app and stops gracefully via `stoppingToken`.
- **Why `CreateScope()`?** A hosted service is a **singleton**, but `DbContext` is **scoped**. You can't inject a scoped service into a singleton, so the dispatcher creates a fresh scope, and therefore a fresh `DbContext`, on every loop.
- **Delivery guarantee: at-least-once.** If the app crashes after `Publish` but before `ProcessedOn` is saved, the message is sent again on restart. So **consumers must be idempotent**, meaning they can safely handle duplicates.

> "The outbox gives **at-least-once delivery** with **eventual consistency**: Payment learns about the order within about 5 seconds, guaranteed, even if RabbitMQ was down at the moment the order was created."

---

## 9. Flow 3: Payment result consumers (closing the saga)

```csharp
public class PaymentCompletedConsumer : IConsumer<PaymentCompletedEvent>
{
    public async Task Consume(ConsumeContext<PaymentCompletedEvent> context)
    {
        var order = await _orderRepository.GetByIdAsync(context.Message.OrderId);
        if (order == null) { _logger.LogWarning(...); return; }
        order.Status = OrderStatus.Paid;
        await _orderRepository.UpdateAsync(order);
    }
}
```
`PaymentFailedConsumer` is the same, but sets `Status = Failed` and logs the reason.

The order's lifecycle:
```
          OrderCreatedEvent ──► Payment
Pending ───────────────────────────────┬──► PaymentCompletedEvent ──► Paid
                                       └──► PaymentFailedEvent    ──► Failed
```

> "Setting the status to `Failed` is the saga's **compensating action**. We can't roll back a distributed transaction across services, so we record the failure and let the business react, for example by notifying the user or restoring stock."

If the order isn't found, the consumers **log a warning and return** instead of throwing. That's a deliberate choice: throwing would make MassTransit retry a message that can never succeed.

---

## 10. Generic Repository pattern: why `IAsyncRepository` and why `RepositoryBase`

The two pieces do different jobs. `IAsyncRepository<T>` is the **contract** (*what* operations exist). `RepositoryBase<T>` is the **shared implementation** (*how* they're done with EF Core). `OrderRepository` then adds only what's specific to orders.

```
IAsyncRepository<T>          ← generic contract: CRUD for ANY entity
        ▲            ▲
        │            │ implements
        │     RepositoryBase<T>      ← generic EF Core implementation, written once
        │            ▲
IOrderRepository     │ inherits
  : IAsyncRepository<Order>          ← adds order-only methods
        ▲            │
        └──── OrderRepository : RepositoryBase<Order>, IOrderRepository
```

### `IAsyncRepository<T>`: the contract
```csharp
public interface IAsyncRepository<T> where T : EntityBase
{
    Task<IReadOnlyList<T>> GetAllAsync();
    Task<IReadOnlyList<T>> GetAllAsync(Expression<Func<T, bool>> predicate);
    Task<T> GetByIdAsync(int id);
    Task<T> AddAsync(T entity);
    Task UpdateAsync(T entity);
    Task DeleteAsync(T entity);
}
```
- **Every entity needs the same basic operations.** Writing the interface once, with `T` as a placeholder, avoids repeating the same six methods for `Order`, `OutboxMessage` or any future entity.
- **Handlers depend on an abstraction, not on EF Core** (Dependency Inversion). Handlers know nothing about `DbContext` or SQL Server. That makes unit testing easy (mock the interface), and the data access could change without touching handlers.
- **"Async":** every method returns a `Task`, so database I/O never blocks a thread. That matters under load.
- **`where T : EntityBase`** guarantees every `T` has an `int Id`, which is what makes a generic `GetByIdAsync(int id)` possible.
- **`IReadOnlyList<T>`** signals "these are query results, don't modify the list".
- **The predicate overload** takes an `Expression<Func<T, bool>>`, not a plain `Func`. EF can **translate an expression tree into SQL** (`WHERE Status = 'Pending'`), so filtering happens in the database instead of in memory.

### `RepositoryBase<T>`: write the EF code once
```csharp
public class RepositoryBase<T> : IAsyncRepository<T> where T : EntityBase
{
    protected readonly OrderContext _dbContext;      // protected → subclasses can use it

    public async Task<T> AddAsync(T entity) { _dbContext.Set<T>().Add(entity); await _dbContext.SaveChangesAsync(); return entity; }
    public async Task<T> GetByIdAsync(int id) => await _dbContext.Set<T>().FindAsync(id);
    public async Task<IReadOnlyList<T>> GetAllAsync() => await _dbContext.Set<T>().AsNoTracking().ToListAsync();
    public async Task UpdateAsync(T entity) { _dbContext.Entry(entity).State = EntityState.Modified; await _dbContext.SaveChangesAsync(); }
    public async Task DeleteAsync(T entity) { _dbContext.Set<T>().Remove(entity); await _dbContext.SaveChangesAsync(); }
}
```
- **No duplicated code (DRY).** The trick is **`_dbContext.Set<T>()`**: it returns the right table for whatever `T` is. One class implements CRUD for **every** entity.
- **Common performance choices in one place:** `AsNoTracking()` for read-only lists, and `FindAsync`, which checks EF's memory cache first.
- **It's a base class:** `_dbContext` is `protected`, so subclasses can write custom queries with the same context.
- **It works even without a subclass.** `Program.cs` registers an **open generic**: `services.AddScoped(typeof(IAsyncRepository<>), typeof(RepositoryBase<>));`. Asking for `IAsyncRepository<OutboxMessage>` gives you a `RepositoryBase<OutboxMessage>` automatically.

### `IOrderRepository` / `OrderRepository`: order-specific additions
```csharp
public interface IOrderRepository : IAsyncRepository<Order>
{
    Task<IEnumerable<Order>> GetOrdersByUserName(string userName);
    Task AddOutboxMessageAsync(OutboxMessage outboxMessage);
}
public class OrderRepository : RepositoryBase<Order>, IOrderRepository { ... }
```
`OrderRepository` writes **only** the extra methods. The CRUD methods come from `RepositoryBase<Order>`. That's the **Open/Closed principle**: the base stays closed for modification, and new behavior comes from extending it.

> "`IAsyncRepository<T>` is the **job description**. `RepositoryBase<T>` is a **generic employee** who can do that job for any entity, because `Set<T>()` gives them the right table. `OrderRepository` is a **specialist** who inherits the generic skills and adds order-specific ones."

### The counter-argument (shows maturity in an interview)
- EF Core's `DbContext` **already is** a Unit of Work, and each `DbSet<T>` **already is** a repository, so a generic wrapper can add little value.
- Every base method calls **`SaveChangesAsync()` by itself**, so a handler that chains two calls gets **two separate transactions**:
  ```csharp
  await _orderRepository.AddAsync(order);                 // SaveChanges #1 → transaction 1
  await _orderRepository.AddOutboxMessageAsync(outbox);   // SaveChanges #2 → transaction 2
  ```
  That's exactly why the **outbox isn't atomic** (improvement #1). If the app crashes between the two calls, the order is saved but no event is ever sent.
- **The fix:** keep the repository abstraction for testability, but take `SaveChanges` out of the repository methods and add a **Unit of Work**. Repositories only *stage* changes, and the handler calls `await _unitOfWork.SaveChangesAsync()` once at the end, so the order and its outbox message commit together.

---

## 11. EF Core: migrations, design-time factory and startup

**Migrations** (`Migrations/` folder) are the database schema as versioned code:
- `20260615…_InitialCreate`: creates the `Orders` table
- `20260616…_AddedOutBoxTable`: adds `Orders.Status` plus the `OutboxMessages` table with an index on `CorrelationId`

> "Migrations put the schema under source control. Every environment gets exactly the same database structure, and changes are incremental and reversible: each migration has `Up()` and `Down()`."

**`OrderContextFactory : IDesignTimeDbContextFactory<OrderContext>`**
> "`dotnet ef migrations add` runs **without starting the app**, so there's no DI container to create the DbContext. This factory tells the EF CLI how to build one by reading `appsettings.json` directly. It's only used at design time, never at runtime."

**Startup: `app.MigrateDatabase<OrderContext>(seeder)` with Polly:**
```csharp
var retry = Policy.Handle<SqlException>()
    .WaitAndRetry(retryCount: 5,
                  sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),  // 2, 4, 8, 16, 32 s
                  onRetry: (ex, span, count) => logger.LogError(...));
retry.Execute(() => { context.Database.Migrate(); seeder(context, services); });
```
> "SQL Server in Docker takes a while to start, much longer than Postgres. Polly retries with **exponential backoff**, waiting longer each time, so the service waits up to about a minute for the database. Then `Database.Migrate()` applies any pending migrations automatically, and the seeder inserts one sample order if the table is empty."

There's a second layer of resilience at runtime:
```csharp
options.UseSqlServer(conn, sql => sql.EnableRetryOnFailure());
```
EF automatically retries **transient** SQL errors, such as brief network blips or deadlocks, during normal queries.

---

## 12. Configuration and Docker

| Setting | Local | Docker |
|---|---|---|
| `ConnectionStrings:OrderingConnectionString` | `Server=localhost,1433;…` | `Server=orderdb,1433;Database=OrderDb;User Id=sa;…;TrustServerCertificate=True` |
| `EventBusSettings:HostAddress` | `amqp://guest:guest@localhost:5672` | `amqp://guest:guest@rabbitmq:5672/` |

- The `orderdb` container runs **SQL Server 2022**, with data in the `orderdb-data` volume.
- `TrustServerCertificate=True` accepts SQL Server's self-signed certificate (fine for dev, not production).
- The service is exposed on port **8003**. Serilog, `CorrelationIdMiddleware` and Swagger are set up as in the other services.

---

## 13. "What would you improve?"

These are real issues found in the code. Items 1–3 matter most.

1. **The outbox isn't actually atomic.** `AddAsync` calls `SaveChangesAsync()` for the order, and then `AddOutboxMessageAsync` calls `SaveChangesAsync()` **again** for the outbox row. Those are **two separate transactions**, so a crash between them leaves an order with no event, which is exactly the problem the outbox exists to prevent. *Fix:* save both in one transaction (one `SaveChangesAsync()`, an explicit transaction, a Unit of Work, or MassTransit's built-in EF Core outbox).
2. **Updating an order re-triggers payment.** `ToOutboxMessageForUpdate` writes a message with `Type = "OrderCreated"`. The dispatcher ignores `Type` and publishes everything as `OrderCreatedEvent`, so editing an order sends a **new "order created" event to Payment**. *Fix:* a separate `OrderUpdatedEvent` type, and have the dispatcher switch on `message.Type`.
3. **The correlation ID gets lost on the main path.** `ToCheckoutOrderCommand(BasketCheckoutEvent)` doesn't copy `CorrelationId`, so orders created from the event get `Guid.Empty`, and the end-to-end trace breaks exactly where it matters most. *Fix:* `CorrelationId = message.CorrelationId`.
4. **`OrderCreatedEvent` is missing `EmailAddress`.** `ToOutboxMessage` never sets it, so Payment or any future notification service has no email address.
5. **Card number and CVV are stored in the database, returned by the GET API (`OrderDto`), and sent in `OrderCreatedEvent`.** This is a serious PCI-DSS violation: CVVs must never be stored at all. *Fix:* use payment-provider tokens, and keep at most the last 4 digits.
6. **Failed outbox messages retry forever.** There's no retry count, `Error` is never filled in, and there's no dead-letter handling. *Fix:* track attempts, save the error, and stop after N attempts.
7. **Multiple instances would publish duplicates.** Two dispatchers can read the same pending rows. *Fix:* row locking (`UPDLOCK, READPAST`) or a "claimed by" column. Also make consumers **idempotent**, for example by having Payment track processed order IDs.
8. **No state-transition rules.** A late `PaymentCompleted` could flip a `Failed` order to `Paid`, and a duplicate event is processed twice. *Fix:* allow only `Pending → Paid/Failed`, and ignore everything else.
9. **Exceptions become 500s on the REST API.** `ValidationException` and `OrderNotFoundException` have no global exception handler mapping them to 400 and 404. Also, `Guid.Parse` on the correlation header throws if the header isn't a valid GUID. `Guid.TryParse` would be safer.
10. **Pipeline order:** `ValidationBehaviour` is registered first, so it's the **outer** behavior, and validation failures never reach `UnhandledExceptionBehaviour`'s logging. Register the exception behavior first if you want those failures logged.
11. **Startup swallows migration failures.** `MigrateDatabase` catches the final exception and continues, so the app runs without a working database. It's better to fail fast and let Docker restart it. Also, `SeedAsync(...).Wait()` is sync-over-async.
12. **No authorization.** Anyone can read any user's orders (`GET /Order/{userName}`) or delete orders. The username should come from the JWT.
13. **Small items:**
    - `CreatedBy = "Sriya"` is hard-coded.
    - Orders have **no line items**, so `TotalPrice` can't be recalculated or checked. Storing `OrderItems` would be the natural next step.
    - The folder name `Behaviour.cs/` looks like a file name.

---

## 14. A 60-second version to memorize

> "Ordering is the system of record for orders, built on ASP.NET Core 9, SQL Server and EF Core. 
Orders are mainly created by events, not REST: a MassTransit consumer listens on `basketcheckout-queue`,
maps the `BasketCheckoutEvent` to a `CheckoutOrderCommand`, and sends it through MediatR. Every command passes through pipeline behaviors:
FluentValidation rules run automatically, and unhandled exceptions are logged centrally, so handlers stay clean.
The handler saves the order as Pending. 
To notify Payment reliably, I use the Transactional Outbox pattern: the `OrderCreatedEvent` is saved as a row in an outbox table,
and a `BackgroundService` polls every 5 seconds and publishes pending rows to RabbitMQ, which gives at-least-once delivery even if 
the broker is down. Payment replies with `PaymentCompleted` or `PaymentFailed` events, and Ordering's consumers move the order to Paid or Failed.
That's a choreography-based saga with no central coordinator. 
On the data side, a generic repository handles CRUD, `SaveChanges` stamps audit fields automatically, the status is stored as a string, and EF migrations are applied at startup with a Polly exponential-backoff retry while SQL Server boots. If I kept working on it, I'd make the order and outbox insert a single transaction, give updates their own event type, carry the correlation ID through the consumer, stop storing card data, and make the consumers idempotent."



T+0s    User clicks Checkout in Angular
T+0s    Ocelot validates JWT, forwards to Basket
T+0s    BasketController dispatches CheckoutBasketCommand
T+0s    Handler fetches basket from Redis ← milliseconds
T+0s    Handler maps dto + basket → BasketCheckoutEvent
T+0s    MassTransit publishes to RabbitMQ "basketcheckout-queue"
T+0s    Handler deletes basket from Redis
T+0s    HTTP 202 Accepted returned to Angular ← user sees this

(async from here — user already got their response)

T+0.1s  BasketOrderingConsumer receives event from queue
T+0.1s  Maps BasketCheckoutEvent → CheckoutOrderCommand
T+0.1s  CheckoutOrderHandler creates Order + OutboxMessage (atomic)
T+0.1s  Order.Status = Pending in SQL Server

T+5s    OutboxMessageDispatcher polls OutboxMessages
T+5s    Finds pending OrderCreatedEvent
T+5s    Publishes to RabbitMQ "ordercreated-queue"
T+5s    OutboxMessage.ProcessedOn = UtcNow

T+5.1s  OrderCreatedConsumer (Payment) receives event
T+6.1s  Task.Delay(1000) — simulated payment processing
T+6.1s  TotalPrice > 0 → publishes PaymentCompletedEvent
T+6.1s  Event goes to "paymentcompleted-queue"

T+6.2s  PaymentCompletedConsumer (Ordering) receives event
T+6.2s  Order.Status = Paid in SQL Server ✅