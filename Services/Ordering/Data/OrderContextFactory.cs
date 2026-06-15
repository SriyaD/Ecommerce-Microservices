using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

//This class exists ONLY for EF Core CLI tools (design-time).
//It has NOTHING to do with your running application.

//Problem it solves:
//When you run "dotnet ef migrations add" or "dotnet ef database update",
//EF Core CLI needs to create an OrderContext instance — BUT your app
//isn't running, so DI container doesn't exist yet.

//This factory tells EF Core CLI: "HERE is how to create an OrderContext
//without the DI container."
//Runtime (app running):
//Program.cs → DI Container → injects OrderContext automatically ✅

//Design Time (CLI tools):
//dotnet ef migrations add → DI doesn't exist → needs OrderContextFactory ✅

namespace Ordering.Data
{
    public class OrderContextFactory : IDesignTimeDbContextFactory<OrderContext>
    {
        public OrderContext CreateDbContext(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json")
                .Build();

            var connectionString = configuration.GetConnectionString("OrderingConnectionString");
            var optionsBuilder = new DbContextOptionsBuilder<OrderContext>();
            optionsBuilder.UseSqlServer(connectionString);
            return new OrderContext(optionsBuilder.Options);
        }
    }
}