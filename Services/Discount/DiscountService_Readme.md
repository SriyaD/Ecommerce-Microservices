# Discount Service — Explained (Interview Style)

## 1. The one-line pitch

> "The Discount Service manages **product coupons**. It's an ASP.NET Core 9 service that exposes a **gRPC API only, with no REST**, and stores coupons in **PostgreSQL**, accessed with **Dapper**. Its main consumer is the Basket service, which asks it 'what's the discount for this product?' every time a cart is saved. Inside, it follows the same **CQRS + MediatR + Repository** structure as the other services."

What makes this service different from the others:
1. It is a **gRPC server**, not a REST API. It's internal-only.
2. It uses a **relational database (PostgreSQL)** with **Dapper**, a micro-ORM, instead of MongoDB, Redis or EF Core.
3. It has its own **startup database setup with a retry loop**.
4. It returns **rich gRPC validation errors** (`google.rpc.BadRequest`).

---

## 2. Where it sits in the system

```
Angular Client ──► API Gateway ──► Basket API
                                      │
                                      │ gRPC (HTTP/2, Protobuf)
                                      ▼
                              Discount Service  (container port 8080, host 8002)
                                      │
                                      │ Dapper + Npgsql (SQL)
                                      ▼
                              PostgreSQL "discountdb" (5432)
                                      ▲
                                 pgAdmin (5050), a UI to inspect the DB
```

- **The browser never calls Discount.** No route in `ocelot.json` points to it. It's a purely **internal** service, which is why gRPC is a good fit.
- Basket finds it at `http://discount:8080` (the Docker service name).

**Why a separate service for discounts at all?**
> "Pricing promotions change often and are managed by a different business team than the product catalog. Keeping them separate means we can change discount rules, deploy, or even replace the service without touching Catalog or Basket. It also gives each service its own database."

---

## 3. Folder structure

| Folder / file | What it is |
|---|---|
| `Protos/discount.proto` | The **gRPC contract**: the source of truth for the API |
| `Services/DiscountService.cs` | The **gRPC endpoint** (plays the role of a controller) |
| `Queries/`, `Commands/` | `GetDiscountQuery`, `CreateDiscountCommand`, `UpdateDiscountCommand`, `DeleteDiscountCommand` |
| `Handlers/` | Business logic + validation for each one |
| `DiscountRepository.cs` + `Repositories/IDiscountRepository.cs` | SQL via **Dapper** |
| `Entities/Coupon.cs` | Database row shape |
| `DTOs/CouponDto.cs` | Internal transfer object |
| `Mappers/CouponMapper.cs` | Protobuf model ↔ commands ↔ entity ↔ DTO |
| `Extensions/DbExtension.cs` | Creates the table and seeds data at startup |
| `Extensions/GrpcErrorHelper.cs` | Builds rich gRPC validation errors |

---

## 4. The data model

```csharp
public class Coupon
{
    public int Id { get; set; }               // SERIAL PRIMARY KEY (auto-increment)
    public string ProductName { get; set; }   // which product this coupon applies to
    public string Description { get; set; }
    public int Amount { get; set; }           // flat amount off, e.g. 500
}
```

The table:
```sql
CREATE TABLE Coupon(
    Id SERIAL PRIMARY KEY,
    ProductName VARCHAR(500) NOT NULL,
    Description TEXT,
    Amount INT)
```

- **One coupon per product**, matched by **product name**.
- `Amount` is a **flat amount off** (not a percentage).

**Why PostgreSQL here, when Catalog uses MongoDB?**
> "Coupons are small, uniform, structured rows: id, product, description, amount. That's a natural fit for a relational table. It also shows **polyglot persistence**: each microservice picks the database that suits its data. Catalog uses Mongo for flexible product documents, Basket uses Redis for fast temporary carts, Discount uses Postgres for structured records, and Ordering uses SQL Server for transactional orders."

---

## 5. The gRPC contract (the heart of the service)

```proto
syntax = "proto3";
option csharp_namespace = "Discount.Grpc.Protos";

service DiscountProtoService {
    rpc GetDiscount(GetDiscountRequest)       returns (CouponModel);
    rpc CreateDiscount(CreateDiscountRequest) returns (CouponModel);
    rpc UpdateDiscount(UpdateDiscountRequest) returns (CouponModel);
    rpc DeleteDiscount(DeleteDiscountRequest) returns (DeleteDiscountResponse);
}

message GetDiscountRequest { string productName = 1; }
message CouponModel {
    int32 id = 1; string productName = 2; string description = 3; int32 amount = 4;
}
message CreateDiscountRequest { CouponModel coupon = 1; }
...
```

How to explain it:
- **`service`** is like an interface: it lists the remote methods.
- **`message`** is like a DTO class.
- **`= 1, = 2 …`** are **field numbers**, not values. On the wire, Protobuf sends the field *number* instead of the field *name*. That's why it's so compact. **Never change or reuse a field number** once it's in use, or old clients will misread the data. You can add new fields freely, which makes the contract **backward compatible**.
- `option csharp_namespace` sets the C# namespace for the generated code.

**Code generation** (`Discount.csproj`):
```xml
<Protobuf Include="Protos\discount.proto" GrpcServices="Server" />
```
- Discount generates the **server** base class: `DiscountProtoService.DiscountProtoServiceBase`.
- Basket has a **copy of the same `.proto`** with `GrpcServices="Client"` and generates `DiscountProtoServiceClient`.
- > "Both sides compile from the same contract, so they always agree on the shape of the data."

The generated code lives in `obj/Debug/net9.0/Protos/` (`Discount.cs` for the messages and `DiscountGrpc.cs` for the service base class). It's regenerated on every build, so never edit it. To change the API, edit the `.proto` and rebuild. The client side of this (where `GetDiscountAsync` comes from) is explained in `Services/Basket/Basket/BasketService_Readme.md`, section 8.

### The gRPC endpoint: `DiscountService.cs`
```csharp
public class DiscountService : DiscountProtoService.DiscountProtoServiceBase
{
    public override async Task<CouponModel> GetDiscount(GetDiscountRequest request, ServerCallContext context)
    {
        var dto = await _mediator.Send(new GetDiscountQuery(request.ProductName));
        return dto.ToModel();
    }
    ...
}
```
> "This is the gRPC equivalent of a controller. It **overrides** the generated base methods, turns the Protobuf request into a MediatR query or command, sends it, and maps the result back to a Protobuf message. It contains no business logic, just like the controllers in Catalog and Basket."

### Hosting gRPC: `Program.cs` + `appsettings.json`
```csharp
builder.Services.AddGrpc();
...
app.UseRouting();
app.UseEndpoints(e => e.MapGrpcService<DiscountService>());
```
```json
"Kestrel": { "EndpointDefaults": { "Protocols": "Http2" } }
```
> "gRPC requires **HTTP/2**. Normally HTTP/2 is negotiated during the TLS handshake. Inside Docker we use plain `http://` with no TLS, so Kestrel has to be told explicitly to speak **HTTP/2 only**. Otherwise the plain-text connection would default to HTTP/1.1 and gRPC calls would fail."

Notice there's **no Swagger and no controllers**. This service has no REST surface at all.

---

## 6. Life of a request: Basket asks for a discount

```
Basket: CreateShoppingCartCommandHandler
   │  _discountGrpcService.GetDiscount("Adidas Quick Force Indoor Badminton Shoes")
   ▼  ── HTTP/2 + Protobuf ──►
Discount: DiscountService.GetDiscount()
   │  new GetDiscountQuery(productName) → MediatR
   ▼
GetDiscountHandler.Handle()
   │
   ▼
DiscountRepository.GetDiscount()
   │  SELECT * FROM Coupon WHERE ProductName = @ProductName   (Dapper)
   ▼
Coupon found?  ──Yes──► return it
               ──No───► return { ProductName: "No Discount", Amount: 0 }
   ▼
Coupon → CouponDto → CouponModel (Protobuf) ──► back to Basket
   ▼
Basket: item.Price -= coupon.Amount
```

**The "No Discount" default is a deliberate design choice** (it's the Null Object pattern):
> "If a product has no coupon, the repository returns a coupon with `Amount = 0` instead of `null` or an error. Basket can then always do `price -= amount` without any special-case code, and a missing coupon never breaks the cart."

---

## 7. Dapper (an interviewer may ask "why not EF Core?")

```csharp
await using var connection = new NpgsqlConnection(_connectionString);
var coupon = await connection.QueryFirstOrDefaultAsync<Coupon>(
    "SELECT * FROM Coupon WHERE ProductName = @ProductName",
    new { ProductName = productName });
```

- **Npgsql** is the .NET driver for PostgreSQL. **Dapper** is a **micro-ORM**: you write the SQL, and Dapper maps the result columns to C# properties.
- **`@ProductName` is a parameter**, not string concatenation. This protects against **SQL injection**, because the value is sent separately from the SQL text.
- **`await using`** disposes the connection when the method ends. Npgsql has **connection pooling**, so "opening a new connection" usually just borrows an existing one from the pool. This is cheap and the correct pattern.

**Why Dapper instead of EF Core?**
> "This service has one tiny table and four simple queries. EF Core would add a DbContext, migrations and change tracking that we don't need. Dapper is lightweight and very fast, close to raw ADO.NET speed, and gives full control of the SQL. For a small, read-heavy lookup service like this, it's the right tool. Ordering, which has richer domain logic, uses EF Core instead."

---

## 8. Database setup at startup (`DbExtension.MigrateDatabase`)

`Program.cs` calls `app.MigrateDatabase<Program>();` before the app starts serving requests:

```
retry = 5
loop:
   open connection to Postgres
   DROP TABLE IF EXISTS Coupon
   CREATE TABLE Coupon(...)
   INSERT 'Adidas Quick Force Indoor Badminton Shoes'           → 500 off
   INSERT 'Yonex VCORE Pro 100 A Tennis Racquet (270gm, Strung)' → 700 off
   success → break
   failure → retry--, try again (throw after 5 failures)
```

**Why the retry loop?**
> "In Docker Compose, `depends_on` only guarantees that the Postgres **container has started**, not that Postgres is **ready to accept connections**. Postgres takes a few seconds to boot. Without retries, the Discount service would crash on startup. The retry loop gives the database time to come up."

The `<TContext>` generic parameter is used only to create a typed `ILogger<TContext>` for log categories.

---

## 9. Validation and rich gRPC errors

The Create and Update handlers validate the input:
```csharp
if (string.IsNullOrWhiteSpace(request.ProductName)) errors["ProductName"] = "Product Name must not be empty.";
if (string.IsNullOrWhiteSpace(request.Description)) errors["Description"] = "...";
if (request.Amount <= 0)                            errors["Amount"]      = "Amount must be greater than zero.";
if (errors.Any()) throw GrpcErrorHelper.CreateValidationException(errors);
```

**gRPC doesn't use HTTP status codes**. It has its own status codes: `OK`, `InvalidArgument`, `NotFound`, `Internal`, etc. The service throws `RpcException` with the right code:

| Situation | gRPC status |
|---|---|
| Bad input | `InvalidArgument` |
| Update target not found | `NotFound` |
| Insert failed | `Internal` |

**`GrpcErrorHelper` goes a step further** and uses Google's **rich error model**:
```csharp
var badRequest = new BadRequest();                      // google.rpc.BadRequest
badRequest.FieldViolations.Add(new FieldViolation { Field = "Amount", Description = "..." });

var status = new Google.Rpc.Status {
    Code = (int)StatusCode.InvalidArgument,
    Message = "Validation Failed",
    Details = { Any.Pack(badRequest) }                  // pack typed details
};
var trailers = new Metadata { { "grpc-status-details-bin", status.ToByteArray() } };
throw new RpcException(new Grpc.Core.Status(StatusCode.InvalidArgument, "Validation errors"), trailers);
```
> "A basic gRPC error carries only a code and a message string. Using the standard `google.rpc.Status` + `BadRequest` types, sent in the `grpc-status-details-bin` trailer, the client gets a **structured list of field errors**, similar to a ProblemDetails response in REST. Any gRPC client in any language can decode it."

That's a nice detail to show you understand gRPC beyond the basics.

---

## 10. Configuration and Docker

- **Postgres container (`discountdb`)**: the `POSTGRES_USER`, `POSTGRES_PASSWORD` and `POSTGRES_DB` environment variables create the user and database automatically on first run. Data is kept in the `postgres_data` volume.
- **pgAdmin** on port 5050 gives a web UI to browse the database.
- **The connection string** is overridden in `docker-compose.override.yml`: `Server=discountdb;Port=5432;Database=DiscountDb;...`
- **Logging:** Serilog plus `CorrelationIdMiddleware` from `Common.Logging`, like every other service.
- **Ports:** container port 8080, exposed as 8002 on the host. That's why Basket's local config uses `http://localhost:8002`.

---

## 11. "What would you improve?"

1. **Bug: `UpdateDiscount` always fails.** The SQL uses `WHERE Id = @Id`, but the parameter object doesn't include `Id`:
   ```csharp
   new { coupon.ProductName, coupon.Description, coupon.Amount }   // missing coupon.Id
   ```
   Npgsql will throw because `@Id` has no value. *Fix:* pass `coupon` itself, or add `coupon.Id`.
2. **Bug: every restart wipes all coupons.** `MigrateDatabase` runs `DROP TABLE IF EXISTS Coupon` on **every startup**, so coupons created through `CreateDiscount` disappear and the Postgres volume is pointless. *Fix:* `CREATE TABLE IF NOT EXISTS`, and seed only when the table is empty, as Catalog does. Better still, use a real migration tool such as **DbUp** or **FluentMigrator**.
3. **The retry loop doesn't wait.** It retries 5 times instantly, which defeats its purpose while Postgres is booting. *Fix:* add `Thread.Sleep(2000)` or exponential backoff (Polly), and use a **Docker healthcheck** with `depends_on: condition: service_healthy`.
4. **Get-discount validation is dead code.** `GetDiscountHandler` builds the error dictionary but never **throws** it. The `if (coupon == null)` check is also unreachable, because the repository never returns null.
5. **Create returns `Id = 0`.** The INSERT doesn't return the generated key. *Fix:* `INSERT ... RETURNING Id` with `ExecuteScalarAsync<int>`.
6. **Coupons are matched by product *name*.** That's fragile: renaming a product in Catalog silently breaks its discount, and the match is case-sensitive and must be exact. *Fix:* match on the Catalog **ProductId** and add a unique index on it.
7. **Business rules are thin.** The discount is a flat `int`, so a 700 discount on a 500 product gives a **negative price**. There are no percentage discounts, no start or expiry dates, and no coupon codes. *Fix:* add `DiscountType`, `ValidFrom` and `ValidTo`, and cap the discount at the price.
8. **No way for admins to manage coupons.** Create, Update and Delete exist only over gRPC, and gRPC reflection isn't enabled. *Fix:* enable **gRPC reflection** (so tools like grpcurl and Postman can discover it), add **JSON transcoding** for a REST admin API, or build a small admin UI.
9. **No authentication.** Any container on the network can create or delete coupons. *Fix:* require a service token (JWT) or mTLS for write operations.
10. **The correlation ID doesn't reach this service.** Basket doesn't forward `x-correlation-id` in gRPC **metadata**, so Discount logs can't be linked to the original request. *Fix:* a gRPC client interceptor in Basket that adds the header.
11. **Secrets in config.** The database password sits in `appsettings.json` and in compose. Use **User Secrets** locally and Docker or Kubernetes secrets or a vault in production.
12. **Small items:**
    - `DeleteDiscount` returns `success = false` instead of a `NotFound` status.
    - `DiscountRepository.cs` sits at the project root instead of in `Repositories/`.
    - There's no caching: coupons rarely change, so a short in-memory cache would remove most database calls.

---

## 12. A 60-second version to memorize

> "Discount is an internal microservice that manages product coupons. It exposes only a gRPC API, defined in a `.proto` contract. Discount generates the server code from it and Basket generates the client code, so both sides compile against the same contract. I chose gRPC because the traffic is service-to-service: binary Protobuf over HTTP/2 is faster than JSON, and the typed contract catches breaking changes at compile time. Because we run without TLS in Docker, Kestrel is set to HTTP/2 only. Coupons are simple structured rows, so they live in PostgreSQL, accessed with Dapper: a lightweight micro-ORM with parameterized SQL and no EF overhead for four simple queries. When a product has no coupon, the repository returns a zero-amount 'No Discount' object, so Basket never needs special-case code. Inside, it's the same CQRS + MediatR + repository layering as the other services. Validation failures return the standard `google.rpc.BadRequest` rich error model, so clients get structured field errors. At startup it creates and seeds the table with a retry loop, because Postgres takes a few seconds to become ready. If I kept working on it, I'd fix the update query's missing Id parameter, stop dropping the table on every restart, match coupons by product ID instead of name, add percentage and expiry rules, and secure the admin operations."
