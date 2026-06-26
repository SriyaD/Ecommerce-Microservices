
---
Ecommerce Microservices — Complete Project Guide

Architecture Overview

This is a .NET 9 microservices e-commerce application with an Angular 17+ frontend. Every service is independent, communicates over well-defined protocols, and runs in Docker. The frontend talks only to the API Gateway, never to individual services directly.

Angular Frontend (port 4200)
        │
        ▼
  Ocelot API Gateway (port 8010)
        │
  ┌─────┼──────┬──────────┬──────────┬──────┐
  ▼     ▼      ▼          ▼          ▼
Catalog Basket Ordering Payment  Identity Discount
(8000) (8001) (8003)   (8004)   (8005)
  │     │  ↕gRPC  │          │
MongoDB Redis Discount  SQL     SQL
       │   Postgres  Server  Server
       │
   RabbitMQ ←───────────── event bus (all async flows)
       │
Elasticsearch + Kibana ← all service logs

---
Shared Patterns Used Everywhere

Before diving into individual services, t single service:

MediatR (CQRS)

Every service uses Commands (write operattions) dispatched via MediatR. Thecontroller never contains logic — it just sends to the mediator.

Controller → _mediator.Send(command/query) → Handler → Repository → Database

CorrelationId Middleware

Every service has a CorrelationIdMiddleware. On every request, it either reads an x-correlation-id header (if one
was already attached by the gateway) or gpropagates through logs so you can traceone user action across all services.

Serilog + Elasticsearch

The Common.Logging shared project configures Serilog for every service. Logs go to the console and also to
Elasticsearch (ecommerce-Logs-{date} inden Kibana at port 5601.

---
Service 1 — Catalog Service

Port: 8000 | Database: MongoDB (port 27017)

What it does

Manages the product catalog — products, brands, and types. This is a pure read-heavy service with full CRUD
support.

Data Model

Product
  ├── Id (MongoDB ObjectId, stored as string)
  ├── Name, Summary, Description, ImageFi
  ├── Price (Decimal128)
  ├── Brand  ← embedded ProductBrand docu
  ├── Type   ← embedded ProductType document
  └── CreatedDate
Brands and Types are stored as their own collections, but they are also embedded inside the Product document
(MongoDB denormalization — no joins neede

How Data is Seeded

On startup, DatabaseSeeder.SeedAsync() ruion is empty. If empty, it reads JSON files from Data/SeedData/:

brands.json  → deserialize → InsertMany into Brands collection
types.json   → deserialize → InsertMany i
products.json→ deserialize → InsertMany into Products collection
              (Id set to null so MongoDB

Seeding order matters: brands and types mcts embed their IDs. This is the MongoDBequivalent of EF Core's HasData().

API Endpoints

All routes are prefixed /api/v1/Catalog:

┌────────┬─────────────────────┬────────────────────────────────────────────────────────────┐
│ Method │        Route        │                               │
├────────┼─────────────────────┼────────────────────────────────────────────────────────────┤
│ GET    │ /GetAllProducts     │ Paginatee/sort/search filters │
├────────┼─────────────────────┼────────────────────────────────────────────────────────────┤
│ GET    │ /{id}               │ Single p                      │
├────────┼─────────────────────┼────────────────────────────────────────────────────────────┤
│ GET    │ /productName/{name} │ Search b                      │
├────────┼─────────────────────┼────────────────────────────────────────────────────────────┤
│ GET    │ /GetAllBrands       │ List all                      │
├────────┼─────────────────────┼────────────────────────────────────────────────────────────┤
│ GET    │ /GetAllTypes        │ List all                      │
├────────┼─────────────────────┼────────────────────────────────────────────────────────────┤
│ GET    │ /brand/{brand}      │ Products                      │
├────────┼─────────────────────┼────────────────────────────────────────────────────────────┤
│ POST   │ /                   │ Create p                      │
├────────┼─────────────────────┼────────────────────────────────────────────────────────────┤
│ PUT    │ /{id}               │ Update p                      │
├────────┼─────────────────────┼────────────────────────────────────────────────────────────┤
│ DELETE │ /{id}               │ Delete p                      │
└────────┴─────────────────────┴────────────────────────────────────────────────────────────┘

Key Packages

- MongoDB.Driver — database access
- MediatR — CQRS dispatcher
- Serilog + Elasticsearch sink — centralized logging

---
Service 2 — Discount Service

Port: 8002 | Database: PostgreSQL (port 5ST)

What it does

Stores coupon/discount records. It is notectly — it is only called internally by the Basket service via gRPC when a user adds an item to the cart.

Why gRPC?

gRPC is a binary protocol — much faster than HTTP/JSON for internal service-to-service calls. Basket calls
Discount hundreds of times (once per cartformance matters. The .proto file definesthe contract; both sides compile from it.

Data Model

Coupon table (PostgreSQL)
  ├── Id (SERIAL PRIMARY KEY)
  ├── ProductName VARCHAR(500)
  ├── Description TEXT
  └── Amount INT

How Data is Seeded

No EF Core migrations — it uses raw Dapper/Npgsql. On startup, DbExtension.MigrateDatabase() runs:
1. Drops the Coupon table if it exists
2. Recreates it
3. Inserts two hardcoded seed rows:
  - Adidas Quick Force Indoor Badminton Shoes → ₹500 off
  - Yonex VCORE Pro 100A Tennis Racquet →

It retries up to 5 times with error handle Postgres may not be ready immediately).

gRPC Operations

The DiscountService class extends DiscounrviceBase:

┌─────────────────────────────┬──────────
│         gRPC Method         │          What it does          │
├─────────────────────────────┼──────────
│ GetDiscount(productName)    │ Returns coupon for a product   │
├─────────────────────────────┼──────────
│ CreateDiscount(coupon)      │ Creates a new coupon           │
├─────────────────────────────┼──────────
│ UpdateDiscount(coupon)      │ Updates a coupon               │
├─────────────────────────────┼──────────
│ DeleteDiscount(productName) │ Deletes coupon by product name │
└─────────────────────────────┴──────────
                                                                                                                ---
Service 3 — Basket Service                                                                                      
Port: 8001 | Cache: Redis (port 6379) | Communicates with: Discount (gRPC) + RabbitMQ                           
What it does                                                                                                    
Manages shopping carts per user. Uses Redis as the storage (not a relational DB) because cart data is ephemeral and needs fast read/write. Each cart is se → JSON of ShoppingCart.

Data Model

ShoppingCart
  ├── UserName (Redis key)
  └── Items: List<ShoppingCartItem>
       ├── ProductId, ProductName
       ├── Price (after discount applied)
       ├── Quantity
       └── ImageFile

Two Key Flows

Flow 1: Add item to cart (with discount)

When a user adds a product or updates theCommandHandler runs:

POST /api/v1/Basket
  → for each item in request:                                                                                           call DiscountGrpcService.GetDiscoun
      item.Price -= coupon.Amount  ← discount applied here                                                          → save updated cart to Redis
  → return updated cart                                                                                          
The DiscountGrpcService is a thin wrapper around the generated gRPC client. It calls the Discount service over    gRPC to fetch the coupon amount. Discount time, so the price stored in Redis isalready the discounted price.                                                                                    
Flow 2: Checkout (event publishing)                                                                              
When the user submits checkout, the CheckoutBasketCommandHandler runs:                                           
POST /api/v1/Basket/Checkout (requires JWT Bearer token)                                                            → fetch current basket from Redis
  → map basket + checkout form → BasketCheckoutEvent                                                                → propagate CorrelationId from HTTP hea
  → publish BasketCheckoutEvent to RabbitMQ (basketcheckout-queue)                                                  → delete basket from Redis
                                                                                                                  The basket is deleted immediately after trder creation is now the Ordering service's responsibility.                                                                                                  
API Endpoints                                                                                                    
┌────────┬─────────────┬───────────────────────────────────────────┐                                              │ Method │    Route    │               Wh
├────────┼─────────────┼───────────────────────────────────────────┤                                              │ GET    │ /{userName} │ Get cart by user
├────────┼─────────────┼───────────────────────────────────────────┤                                              │ POST   │ /           │ Create or update
├────────┼─────────────┼───────────────────────────────────────────┤                                              │ DELETE │ /{userName} │ Delete cart
├────────┼─────────────┼───────────────────────────────────────────┤                                              │ POST   │ /Checkout   │ Trigger checkout
└────────┴─────────────┴───────────────────────────────────────────┘                                             
---                                                                                                               Service 4 — Ordering Service
                                                                                                                  Port: 8003 | Database: SQL Server (port 1nsumers
                                                                                                                  This is the most complex service. It usesplements the Transactional Outbox Pattern.
                                                                                                                  Data Model
                                                                                                                  Orders table
  ├── Id (int, auto-increment)                                                                                      ├── UserName, FirstName, LastName, Emai
  ├── AddressLine, Country, State, ZipCode                                                                          ├── CardName, CardNumber, Expiration, C
  ├── TotalPrice                                                                                                    └── Status: Pending(0) / Paid(1) / Fail
                                                                                                                  OutboxMessages table
  ├── Id, Type, Content (JSON), CorrelationId                                                                       ├── OccurredOn
  └── ProcessedOn (null = not yet published)                                                                     
How Data is Seeded                                                                                               
EF Core migrations (InitialCreate + AddedOutBoxTable) run on startup via MigrateDatabase().                       OrderContextSeed.SeedAsync() inserts one  if the Orders table is empty.
                                                                                                                  The Outbox Pattern — Why?
                                                                                                                  The problem: after creating an order in Sh OrderCreatedEvent to RabbitMQ. If thepublish fails after the DB commit, you have an order but no payment triggered. If you rollback the DB after a     failed publish, you've lost the order.
                                                                                                                  Solution — write to the DB and the messag:
                                                                                                                  BasketCheckoutEvent received from RabbitM
  → CheckoutOrderHandler:                                                                                               1. INSERT Order into SQL Server
      2. INSERT OutboxMessage (JSON of OrderCreatedEvent) into SQL Server                                                  → both in same DB transaction (a
  → return order ID                                                                                              
Then a background service (OutboxMessageDispatcher) runs every 5 seconds:                                         Loop every 5 seconds:
  SELECT TOP 20 FROM OutboxMessages WHERE ProcessedOn IS NULL ORDER BY OccurredOn                                   for each message:
    deserialize Content → OrderCreatedEvent                                                                           publish to RabbitMQ (ordercreated-que
    SET ProcessedOn = NOW                                                                                           SaveChanges
                                                                                                                  This guarantees at-least-once delivery — ox holds the messages until it comes back.
                                                                                                                  Three Event Consumers
                                                                                                                  ┌──────────────────────────┬────────────────────────────┐
│         Consumer         │         Queue          │          What it does          │                            ├──────────────────────────┼────────────────────────────┤
│ BasketOrderingConsumer   │ basketcheckout-queue   │ Creates order + outbox message │                            ├──────────────────────────┼────────────────────────────┤
│ PaymentCompletedConsumer │ paymentcompleted-queue │ Sets Order.Status = Paid       │                            ├──────────────────────────┼────────────────────────────┤
│ PaymentFailedConsumer    │ paymentfailed-queue    │ Sets Order.Status = Failed     │                            └──────────────────────────┴────────────────────────────┘
                                                                                                                  API Endpoints
                                                                                                                  ┌────────┬─────────────┬─────────────────
│ Method │    Route    │          What it does           │                                                        ├────────┼─────────────┼─────────────────
│ GET    │ /{userName} │ Get all orders for a user       │                                                        ├────────┼─────────────┼─────────────────
│ POST   │ /           │ Manually create order (testing) │                                                        ├────────┼─────────────┼─────────────────
│ PUT    │ /           │ Update order                    │                                                        ├────────┼─────────────┼─────────────────
│ DELETE │ /{id}       │ Delete order                    │                                                        └────────┴─────────────┴─────────────────
                                                                                                                  ---
Service 5 — Payment Service                                                                                      
Port: 8004 | No database | Pattern: Consumer + Publisher                                                         
What it does                                                                                                     
Simulates payment processing. It has no database — it is purely a message consumer/publisher.                    
Flow                                                                                                             
OrderCreatedEvent arrives on ordercreated-queue                                                                     → simulate 1 second processing delay
  → if TotalPrice > 0:                                                                                                  publish PaymentCompletedEvent (ordecompleted-queue
  → else:                                                                                                               publish PaymentFailedEvent (orderIdymentfailed-queue
                                                                                                                  In a real system this would call Stripe/Rif the price is positive.
                                                                                                                  ---
Service 6 — Identity Service                                                                                     
Port: 8005 | Database: SQL Server (port 1434) | Auth: ASP.NET Identity + JWT                                     
What it does                                                                                                     
Handles user registration and login. Issues JWT tokens that the frontend stores in localStorage and sends as      Authorization: Bearer <token> on protecte
                                                                                                                  Data Model
                                                                                                                  Uses ASP.NET Identity's default schema (Uus custom fields:
ApplicationUser : IdentityUser                                                                                      ├── (all standard Identity fields: Id, ...)
  ├── FirstName                                                                                                     └── LastName
                                                                                                                  EF Core migrations manage the schema on s
                                                                                                                  Endpoints
                                                                                                                  ┌────────┬────────────────────┬──────────
│ Method │       Route        │    What it does     │                                                             ├────────┼────────────────────┼──────────
│ POST   │ /api/auth/register │ Create user account │                                                             ├────────┼────────────────────┼──────────
│ POST   │ /api/auth/login    │ Returns JWT token   │                                                             └────────┴────────────────────┴──────────
                                                                                                                  JWT token contains: sub (email), name (us
Token validity: 60 minutes                                                                                       
---                                                                                                               Service 7 — API Gateway (Ocelot)
                                                                                                                  Port: 8010 | No database | Routes all tra
                                                                                                                  What it does
                                                                                                                  Single entry point for the frontend. Everto port 8010. Ocelot readsocelot.Local.json (local dev) or ocelot.Development.json (Docker) and routes each request to the appropriate      downstream service.
                                                                                                                  Route Mapping
                                                                                                                  ┌───────────────────────────────┬─────────────────────────────┐
│        Frontend calls         │                     Ocelot routes to                     │                      ├───────────────────────────────┼─────────────────────────────┤
│ GET /Catalog/GetAllProducts   │ http://catalog:8000/api/v1/Catalog/GetAllProducts        │                      ├───────────────────────────────┼─────────────────────────────┤
│ POST /Basket                  │ http://basket:8001/api/v1/Basket                         │                      ├───────────────────────────────┼─────────────────────────────┤
│ POST /Basket/Checkout         │ http://basket:8001/api/v1/Basket/Checkout (JWT required) │                      ├───────────────────────────────┼─────────────────────────────┤
│ GET /Order/{userName}         │ http://ordering:8003/api/v1/Order/{userName}             │                      ├───────────────────────────────┼─────────────────────────────┤
│ POST /identity/api/auth/login │ http://identity:8005/api/auth/login                      │                      └───────────────────────────────┴─────────────────────────────┘
                                                                                                                  JWT Enforcement at the Gateway
                                                                                                                  The POST /Basket/Checkout route has "AuthicationProviderKey": "Bearer"} in theOcelot config. The gateway validates the JWT using the same secret key as the Identity service before forwarding  the request. If the token is missing or ithe request never reaches the Basketservice.                                                                                                         
CORS                                                                                                             
Configured in appsettings.json under Cors:AllowedOrigins. The Angular dev server origin is whitelisted here.     
---                                                                                                               Event Bus — RabbitMQ
                                                                                                                  Ports: 5672 (AMQP), 15672 (Management UI)
                                                                                                                  All services use MassTransit as the Rabbihandles serialization, retries, dead-letter queues, and consumer registration.                                                                               
Queues and Events                                                                                                
┌────────────────────────┬─────────────────┬──────────────────┐                                                   │       Queue name       │  Published by
├────────────────────────┼─────────────────┼──────────────────┤                                                   │ basketcheckout-queue   │ Basket service
├────────────────────────┼─────────────────┼──────────────────┤                                                   │ ordercreated-queue     │ Ordering outbo
├────────────────────────┼─────────────────┼──────────────────┤                                                   │ paymentcompleted-queue │ Payment servic
├────────────────────────┼─────────────────┼──────────────────┤                                                   │ paymentfailed-queue    │ Payment servic
└────────────────────────┴─────────────────┴──────────────────┘                                                  
Full Checkout Event Flow                                                                                         
1. User submits checkout form (Angular)                                                                              ↓
2. POST /Basket/Checkout → API Gateway → Basket Service                                                              (JWT validated at gateway)
   ↓                                                                                                              3. Basket publishes BasketCheckoutEvent →
   Basket deletes cart from Redis                                                                                    ↓
4. Ordering.BasketOrderingConsumer receives event                                                                    → Creates Order in SQL Server (Status
   → Writes OutboxMessage (JSON of OrderCreatedEvent) to SQL Server                                                  ↓
5. OutboxMessageDispatcher (runs every 5s)                                                                           → Reads unpublished OutboxMessages
   → Publishes OrderCreatedEvent → ordercreated-queue                                                                → Marks messages as processed
   ↓                                                                                                              6. Payment.OrderCreatedConsumer receives
   → Simulates payment (1s delay)                                                                                    → TotalPrice > 0?
       YES → publishes PaymentCompletedEvent → paymentcompleted-queue                                                    NO  → publishes PaymentFailedEvent
   ↓                                                                                                              7a. Ordering.PaymentCompletedConsumer rec
    → Order.Status = Paid                                                                                             → SaveChanges
7b. Ordering.PaymentFailedConsumer receives event                                                                     → Order.Status = Failed
    → SaveChanges                                                                                                
---                                                                                                               Docker Setup
                                                                                                                  docker-compose.yml — What images to build
                                                                                                                  Defines all services and their Dockerfile for data persistence.
                                                                                                                  docker-compose.override.yml — Environment
                                                                                                                  Injects environment variables (connection address, JWT settings) into eachcontainer. Also maps container ports to host ports.                                                              
Port Map                                                                                                         
┌─────────────────────────┬──────────────┬─────────────────────────────┐                                          │        Container        │  Host Port    │
├─────────────────────────┼──────────────┼─────────────────────────────┤                                          │ catalogdb (MongoDB)     │ 27017         │
├─────────────────────────┼──────────────┼─────────────────────────────┤                                          │ basketdb (Redis)        │ 6379          │
├─────────────────────────┼──────────────┼─────────────────────────────┤                                          │ discountdb (PostgreSQL) │ 5432          │
├─────────────────────────┼──────────────┼─────────────────────────────┤                                          │ orderdb (SQL Server)    │ 1433          │
├─────────────────────────┼──────────────┼─────────────────────────────┤                                          │ identitydb (SQL Server) │ 1434          │
├─────────────────────────┼──────────────┼─────────────────────────────┤                                          │ rabbitmq                │ 5672 / 15672  │
├─────────────────────────┼──────────────┼─────────────────────────────┤                                          │ pgadmin                 │ 5050          │
├─────────────────────────┼──────────────┼─────────────────────────────┤                                          │ portainer               │ 9090          │
├─────────────────────────┼──────────────┼─────────────────────────────┤                                          │ catalog                 │ 8000          │
├─────────────────────────┼──────────────┼─────────────────────────────┤                                          │ basket                  │ 8001          │
├─────────────────────────┼──────────────┼─────────────────────────────┤                                          │ discount                │ 8002          │
├─────────────────────────┼──────────────┼─────────────────────────────┤                                          │ ordering                │ 8003          │
├─────────────────────────┼──────────────┼─────────────────────────────┤                                          │ payment                 │ 8004          │
├─────────────────────────┼──────────────┼─────────────────────────────┤                                          │ identity                │ 8005          │
├─────────────────────────┼──────────────┼─────────────────────────────┤                                          │ ocelot.apigateway       │ 8010          │
├─────────────────────────┼──────────────┼─────────────────────────────┤                                          │ elasticsearch           │ 9200          │
├─────────────────────────┼──────────────┼─────────────────────────────┤                                          │ kibana                  │ 5601          │
└─────────────────────────┴──────────────┴─────────────────────────────┘                                         
depends_on chains                                                                                                
- catalog waits for catalogdb                                                                                     - basket waits for basketdb + rabbitmq
- discount waits for discountdb                                                                                   - ordering waits for orderdb + rabbitmq
- identity waits for identitydb                                                                                   - ocelot.apigateway waits for all service
                                                                                                                  ---
Frontend — Angular                                                                                               
Tech Stack                                                                                                       
- Angular 17+ standalone components + signals                                                                     - Angular Router with lazy-loaded feature
- Angular Material (snackbar notifications on checkout)                                                           - HttpClient for all API calls
- All calls go to http://localhost:8010 (Ocelot gateway)                                                         
Routing                                                                                                          
/              → Home (landing page)                                                                              /store         → Store (product listing)
/store/product/:id → Product Details                                                                              /store/basket  → Shopping Cart
/store/checkout → Checkout Form        [authGuard]                                                                /store/checkout-success → Order Success [
/store/orders  → My Orders             [authGuard]                                                                /auth/login    → Login page
/auth/register → Register page                                                                                   
The authGuard redirects to /auth/login if there is no JWT token in localStorage.                                 
Key Services                                                                                                     
ProductService — talks to Catalog via gateway                                                                     - getAllProducts(page, size, brandId, typuery params for filtering/pagination
- getAllBrands(), getAllTypes(), getProductById(id)                                                              
BasketService — talks to Basket via gateway                                                                       - Maintains a signal (basketSignal) for r
- Also persists basket to localStorage as a fallback (cart survives page refresh)                                 - getBasket(userName), updateBasket(baske
- setBasket(basket) — updates the signal + localStorage + badge count                                            
CheckoutService — talks to Basket Checkout via gateway                                                            - Attaches Authorization: Bearer <token>
- Posts CheckoutPayload which mirrors the BasketCheckoutEvent shape                                              
AuthService — talks to Identity via gateway                                                                       - Stores JWT token in localStorage and in
- isLoggedIn() — checks if token exists                                                                           - getUserName() — decodes JWT with jwt-declaim
                                                                                                                  Store Page Features
                                                                                                                  - Brand filter, Type filter, sort dropdow to backend query params
- Pagination with pageSize=10                                                                                     - "Add to Cart" button: fetches current ctem exists, saves back
                                                                                                                  Checkout Flow
                                                                                                                  1. User fills form: name, address, card d
2. onSubmit() calls CheckoutService.checkout(payload)                                                             3. On success: clears basket (signal + loOrder placed", navigates to/store/checkout-success with orderId in query params                                                              4. On failure: shows error snackbar
                                                                                                                  ---
Shared Library — EventBus.Messages                                                                               
A separate .NET class library project referenced by Basket, Ordering, and Payment. Contains:                     
EventBus.Messages/                                                                                                  Common/
    EventBusConstant.cs  ← queue name constants                                                                     Events/
    BaseIntegrationEvent.cs  ← has CorrelationId (Guid)                                                               BasketCheckoutEvent.cs   ← full check
    OrderCreatedEvent.cs     ← order created, triggers payment                                                        PaymentCompletedEvent.cs ← orderId +
    PaymentFailedEvent.cs    ← orderId + correlationId + reason                                                  
Because all event classes are in one shared project, adding a new field to BasketCheckoutEvent will cause compile errors in all consumers — enforcing that

---
Observability

Correlation ID — generated at the gateway (or by any service if no header present), propagated in x-correlation-id header across HTTP callssages. You can filter Kibana byCorrelationId to see every log line across every service for one user's checkout.

Serilog — structured logging in every service. Log entries include ApplicationName, EnvironmentName, and enriched
exception details.

Elasticsearch + Kibana — all logs flow tog sink. Index pattern:ecommerce-Logs-{YYYY.MM.dd}. Browse in Kibana at port 5601.

Portainer — visual Docker container manager at port 9090. See container health, logs, and resource usage without CLI.
