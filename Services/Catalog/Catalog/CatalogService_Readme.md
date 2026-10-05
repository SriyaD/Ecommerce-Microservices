# Catalog Service — Explained (Interview Style)

## 1. The one-line pitch

> "The Catalog Service is the microservice that owns all **product information** in my e-commerce app: products, brands and product types. It's an **ASP.NET Core 9 Web API** backed by **MongoDB**. It uses the **CQRS pattern with MediatR** and the **Repository pattern**, runs in **Docker**, and is reached through an **Ocelot API Gateway**."

If you only get 30 seconds, stop there. Everything below is detail for the follow-up questions.

---

## 2. Where it sits in the overall system

```
Angular Client
     │
     ▼
Ocelot API Gateway        /Catalog/GetAllProducts
     │
     ▼
Catalog API (.NET 9)      /api/v1/Catalog/GetAllProducts
     │
     ▼
MongoDB (catalogdb container, port 27017)
   ├── Products
   ├── Brands
   └── Types
```

- **The client never calls Catalog directly.** It calls the gateway, and `ocelot.json` maps `/Catalog/...` (upstream) to `/api/v1/Catalog/...` (downstream).
- Each microservice has **its own database** (database-per-service): Catalog uses MongoDB, Basket uses Redis, Discount uses Postgres, and Ordering uses SQL Server.

**Why MongoDB for the catalog?**
> "Catalog data is read far more often than it's written. It's naturally document-shaped: a product together with its brand and type. Products in different categories may need different attributes later, and MongoDB's flexible schema handles that without migrations."

---

## 3. The folder structure (the layers)

| Folder | What it is | Plain-English role |
|---|---|---|
| `Entities/` | `BaseEntity`, `Product`, `ProductBrand`, `ProductType` | The shape of the data **as stored in MongoDB** |
| `Controllers/` | `CatalogController` | The **front door**: receives HTTP requests |
| `Queries/` + `Commands/` | `GetAllProductsQuery`, `CreateProductCommand`, … | **Messages** that describe what you want to do |
| `Handlers/` | `GetAllProductsHandler`, … | The **workers** that carry out each message |
| `Repositories/` | `IProductRepository`, `ProductRepository`, … | The **only code that talks to MongoDB** |
| `Responses/` + `DTOs/` | `ProductResponse`, `ProductDto`, `CreateProductDto` | The shape of data **going in and out of the API** |
| `Mappers/` | `ProductMapper`, … | **Converters** between entities, commands, responses and DTOs |
| `Specifications/` | `CatalogSpecParams`, `Pagination<T>` | Settings for **paging, sorting, filtering and search** |
| `Data/` | `DatabaseSeeder` + JSON files | **Fills the database** with starter data |

---

## 4. Life of a request (the most important thing to explain)

Take `GET /api/v1/Catalog/GetAllProducts?pageIndex=1&pageSize=10&brandId=abc&sort=priceAsc`:

```
1. CatalogController.GetAllProducts(CatalogSpecParams)
       │  wraps the params in a message
       ▼
2. new GetAllProductsQuery(params)  ──►  _mediator.Send(query)
       │  MediatR finds the matching handler
       ▼
3. GetAllProductsHandler.Handle()
       │  asks the repository for data
       ▼
4. ProductRepository.GetProducts(params)
       │  builds a Mongo filter + sort + skip/limit
       ▼
5. MongoDB returns Product entities
       ▼
6. Handler maps Pagination<Product> → Pagination<ProductResponse>
       ▼
7. Controller returns 200 OK with JSON
```

How to say it:
> "The controller is very thin. It creates a query or command object and sends it through MediatR. MediatR routes it to exactly one handler. The handler holds the business logic and uses the repository for data access. The repository is the only class that knows MongoDB exists. On the way out, mappers convert the database entity into a response object, so the database model is never exposed directly."

---

## 5. The design patterns, one at a time

### a) CQRS (Command Query Responsibility Segregation)
Reads and writes are split into separate classes:
- **Queries** (read, change nothing): `GetAllProductsQuery`, `GetProductByIdQuery`, `GetProductByNameQuery`, `GetProductByBrandQuery`, `GetAllBrandsQuery`, `GetAllTypesQuery`
- **Commands** (change data): `CreateProductCommand`, `UpdateProductCommand`, `DeleteProductByIdCommand`

> "Each operation gets its own small class instead of one big `ProductService` with 10 methods. That makes each piece easy to read, test and change on its own. If needed later, reads and writes could even scale separately."

### b) Mediator pattern (MediatR library)
```csharp
public record GetAllProductsQuery(CatalogSpecParams CatalogSpecParams)
    : IRequest<Pagination<ProductResponse>>;
```
- `IRequest<T>` means "this message returns a `T`".
- `IRequestHandler<GetAllProductsQuery, Pagination<ProductResponse>>` is the handler for it.
- `Program.cs` registers every handler automatically: `AddMediatR(cfg => cfg.RegisterServicesFromAssemblies(...))`.

> "The controller only depends on `IMediator`. It doesn't know which handler runs. That keeps the controller decoupled. It also gives a place to add cross-cutting behaviors like logging or validation in one spot (MediatR pipeline behaviors)."

**Why queries are `record`s:** they're immutable data carriers. They get created, sent and handled, and never modified.

### c) Repository pattern
```csharp
public interface IProductRepository
{
    Task<Pagination<Product>> GetProducts(CatalogSpecParams specParams);
    Task<Product> GetProduct(string productId);
    Task<Product> CreateProduct(Product product);
    Task<bool> UpdateProduct(Product product);
    Task<bool> DeleteProduct(string id);
    ...
}
```
- Registered in `Program.cs` with `AddScoped<IProductRepository, ProductRepository>()`.
- Handlers depend on the **interface**, so in unit tests you can mock it and skip a real MongoDB.

> "If we ever switched from Mongo to something else, only the repository would change. Handlers and controllers wouldn't."

### d) Dependency Injection
Everything is wired in `Program.cs`. The controller gets `IMediator`, handlers get `IProductRepository`, and repositories get `IConfiguration` to read the connection string.

### e) Mapper pattern (hand-written extension methods)
`ProductMapper` has extension methods such as `product.ToResponse()`, `command.ToEntity(brand, type)` and `dto.ToCommand(id)`.
> "I wrote the mappers by hand instead of using AutoMapper. They're explicit, easy to debug, have no reflection magic, and are fast."

---

## 6. The data model

### `BaseEntity`
```csharp
public class BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }
}
```
- `[BsonId]` maps the property to MongoDB's required `_id` field.
- `[BsonRepresentation(BsonType.ObjectId)]` stores it as a native 12-byte **ObjectId** in the database, while C# sees a plain **string**. The driver converts between the two.
- Why a string in C#: routes like `GET /api/v1/Catalog/{id}` and JSON work naturally, and no Mongo types leak to the frontend. The database still keeps compact, indexed ObjectIds.
- If `Id` is `null` on insert, MongoDB generates a new ObjectId.
- `Product`, `ProductBrand` and `ProductType` all inherit from it, so every collection uses the same ID scheme.

### `ProductBrand` / `ProductType`
- Each has just an `Id` (inherited) and a `Name` (`[BsonElement("Name")]`, which pins the stored field name).
- Each lives in its own collection (Brands and Types). These are the lookup lists the frontend uses for filter dropdowns.

### `Product`
| Property | Notes |
|---|---|
| `Id` | Inherited, stored as ObjectId `_id` |
| `Name`, `Summary`, `Description` | Plain strings |
| `ImageFile` | Stores only a filename or path; the frontend builds the image URL |
| `Brand`, `Type` | **Embedded sub-documents** |
| `Price` | `decimal` stored as **Decimal128** |
| `CreatedDate` | `DateTimeOffset` |

- **`Price` as `Decimal128`:** by default the driver stores `decimal` as a string, which breaks numeric sorting (`"100"` < `"20"`). `Decimal128` gives exact money arithmetic and correct price sorting and range queries.
- **`CreatedDate`:** `Program.cs` registers global serializers:
  ```csharp
  BsonSerializer.RegisterSerializer(new DateTimeOffsetSerializer(BsonType.String));
  BsonSerializer.RegisterSerializer(new GuidSerializer(BsonType.String));
  ```
  So `CreatedDate` is stored as an **ISO string** in MongoDB. It's readable, but it isn't a native Mongo date, so date-range queries are less convenient.

**Embedding vs referencing** is the key interview point:
```json
{
  "_id": ObjectId("..."),
  "Name": "Adidas Quick Force",
  "Brand": { "_id": ObjectId("..."), "Name": "Adidas" },
  "Type":  { "_id": ObjectId("..."), "Name": "Shoes" },
  "Price": NumberDecimal("150.00")
}
```
> "Instead of storing only a BrandId and joining, like in SQL, each product holds a small copy of its brand and type. One read returns everything the UI needs. The trade-off is duplication: renaming a brand means updating the products too. Brands rarely change, so that's acceptable."

---

## 7. The API endpoints

Base route: `api/v1/Catalog` (the `v1` leaves room for API versioning).

| Method | Route | What it does | Returns |
|---|---|---|---|
| GET | `GetAllProducts?pageIndex&pageSize&brandId&typeId&sort&search` | Paged, filtered, sorted list | `Pagination<ProductResponse>` |
| GET | `{id}` | One product | `ProductResponse` |
| GET | `productName/{name}` | Search by name (case-insensitive) | list, or 404 |
| GET | `brand/{brand}` | Products of one brand name | list |
| GET | `GetAllBrands` | All brands (for filter dropdowns) | list |
| GET | `GetAllTypes` | All types | list |
| POST | `/` | Create a product | created product |
| PUT | `{id}` | Update a product | 204, or 404 |
| DELETE | `{id}` | Delete a product | 204, or 404 |

---

## 8. Paging, filtering, sorting and search (a strong interview topic)

**`CatalogSpecParams`** holds what the client asks for:
```csharp
private const int MaxPageSize = 70;
public int PageSize { get => _pageSize; set => _pageSize = (value > MaxPageSize) ? MaxPageSize : value; }
```
> "The page size is capped at 70, so a client can't request `pageSize=1000000` and overload the database. It's a small defensive check."

**`ProductRepository.GetProducts`** builds the query step by step:
```csharp
var filter = builder.Empty;                                   // start with "match all"
if (Search)  filter &= name contains search (case-insensitive)
if (BrandId) filter &= Brand.Id == BrandId                   // works because Brand is embedded
if (TypeId)  filter &= Type.Id  == TypeId
```
Then:
1. `CountDocumentsAsync(filter)` gives the **total** matching items, so the UI can show "Page 1 of 5".
2. Sort: `priceAsc`, `priceDesc`, or the default of **Name ascending**.
3. `Skip(PageSize * (PageIndex - 1)).Limit(PageSize)` returns only the requested page.

It returns `Pagination<T> { PageIndex, PageSize, Count, Data }`, which is everything the Angular paginator needs.

> "Filtering and paging happen **in the database**, not in memory, so we only transfer the 10 products we actually show."

---

## 9. Create and update flow (business logic in handlers)

**Create** (`CreateProductCommandHandler`):
1. The client sends `BrandId` and `TypeId`, **not** full brand and type objects.
2. The handler **looks them up** in the Brands and Types collections.
3. If either one doesn't exist, it throws "Invalid Brand or Type specified".
4. It maps the command to a `Product` with the real brand and type embedded and sets `CreatedDate = UtcNow`.
5. It inserts the product. MongoDB generates the ObjectId.

> "The client only sends IDs, and the server validates them and embeds the real data. That way a client can't send a fake brand name."

**Update** is the same idea, plus it first checks that the product exists, and it **keeps the original `CreatedDate`**. The repository uses `ReplaceOneAsync`, which replaces the whole document.

**Delete** uses `DeleteOneAsync` and returns `true` only if `DeletedCount > 0`. The controller returns **204 No Content** on success and **404** if nothing was deleted.

---

## 10. Database seeding

`DatabaseSeeder.SeedAsync` runs **once at startup** from `Program.cs`:
```
Brands empty?   → load brands.json   → InsertMany
Types empty?    → load types.json    → InsertMany
Products empty? → load products.json → reset Id (Mongo generates new ones) → InsertMany
```
- It's **idempotent**: it only seeds empty collections, so restarting the app never creates duplicates.
- It uses `InsertManyAsync`, one bulk insert instead of many single inserts.
- The order matters: brands and types first, because products embed them.

---

## 11. Configuration, logging and Docker

- **Config:** `appsettings.json` points to `mongodb://localhost:27017` for local runs. In Docker, `docker-compose.override.yml` overrides it with environment variables like `DatabaseSettings__ConnectionString=mongodb://catalogdb:27017`. The `__` maps to the `:` nesting in JSON, and `catalogdb` is the container name, which Docker's network resolves.
- **Logging:** **Serilog** comes from a shared `Common.Logging` project, which points to Elasticsearch via `ElasticConfiguration:Uri`. A **`CorrelationIdMiddleware`** tags each request with an ID so you can trace one user request across multiple microservices in the logs.
- **Swagger** is enabled for interactive API docs and testing.
- **Dockerfile:** a **multi-stage build**. The SDK image builds and publishes the app, and the final image contains only the small ASP.NET runtime plus the compiled DLLs. It runs as a non-root user (`USER $APP_UID`) and exposes port 8080.
- **MongoDB data** lives in a named volume (`mongo_data`), so it survives container restarts.

---

## 12. "What would you improve?" (interviewers almost always ask)

1. **`MongoClient` is created in every repository constructor**, and the repositories are *scoped*, so a new client is created on every request. MongoDB recommends **one singleton `MongoClient`**, since it manages its own connection pool. *Fix:* register `IMongoClient` as a singleton and inject it.
2. **Bug: update drops `Description`.** `ProductMapper.ToUpdateEntity` never copies `Description`, and since `ReplaceOneAsync` replaces the whole document, every update erases the description.
3. **Bug: `ToDto()` sets `CreatedDate = DateTimeOffset.UtcNow`** instead of `product.CreatedDate`, so the search-by-name endpoint shows the wrong date.
4. **Updating with identical data returns 404.** `UpdateProduct` checks `ModifiedCount > 0`, so saving unchanged data looks like "not found". `MatchedCount > 0` would be the correct check.
5. **GetById for a missing ID returns `200` with an empty body** instead of **404**. Also, an ID that isn't a valid ObjectId format will throw.
6. **Errors become 500s.** Handlers throw `ApplicationException` and `KeyNotFoundException`, but there's no global exception middleware to turn them into 400 or 404 responses.
7. **Regex injection in name search.** `GetProductsByName` puts user input straight into a regex. It should be escaped with `Regex.Escape(name)`.
8. **No validation pipeline.** `CreateProductDto` has `[Required]` and `[Range]` attributes, but the controller binds `CreateProductCommand` directly, so those rules never run. *Fix:* bind the DTO, or add FluentValidation as a MediatR pipeline behavior.
9. **No auth on write endpoints.** Anyone can create, update or delete products. Admin-only endpoints should have `[Authorize(Roles="Admin")]`.
10. **No caching.** Catalog is read-heavy, so caching brands and types (which rarely change) in memory or Redis would be cheap and effective.
11. **No indexes.** Adding indexes on `Name`, `Brand._id` and `Type._id` would speed up filtering and search as the data grows.
12. **No POST semantics.** Create returns `200 OK`, but REST convention is **`201 Created`** with a `Location` header (`CreatedAtAction`).

---

## 13. A 60-second version to memorize

> "Catalog is an ASP.NET Core 9 microservice that owns product, brand and type data in MongoDB. Requests come through an Ocelot gateway to a thin controller, which sends a query or command via MediatR. That's CQRS: reads and writes are separate classes, each with one handler. Handlers hold the business logic, like checking that the brand and type exist before creating a product, and call a repository, the only layer that talks to MongoDB. Products embed their brand and type, so one read gets everything the UI needs, and filtering by brand is a simple field match. The listing endpoint supports server-side paging with a max page size, filtering, search and price sorting, and returns a total count for the paginator. On startup, an idempotent seeder loads JSON data into empty collections. It runs in Docker with a multi-stage build, logs through Serilog to Elasticsearch with correlation IDs, and gets its config from environment variables. If I kept working on it, I'd make the MongoClient a singleton, add global exception handling and validation, secure the write endpoints, add caching and indexes, and fix a couple of mapping bugs I found."
