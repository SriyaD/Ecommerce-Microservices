using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Polly;
using System.Diagnostics.Metrics;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

//This extension method runs on app startup and does TWO things:
//1.Applies any pending EF Core migrations to SQL Server automatically
//2. Seeds initial data if needed

//With a RETRY STRATEGY — if SQL Server isn't ready yet (e.g. Docker
//container still starting), it waits and retries up to 5 times
//using exponential backoff before giving up.
namespace Ordering.Extensions
{
    public static class DbExtension
    {
        public static IHost MigrateDatabase<TContext>(this IHost host, Action<TContext, IServiceProvider> seeder)
            where TContext : DbContext
        {
            using (var scope = host.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                var logger = services.GetRequiredService<ILogger<TContext>>();
                var context = services.GetService<TContext>();


                try
                {
                    logger.LogInformation($"Started Db Migration: {typeof(TContext).Name}");
                    //retry strategy
                    var retry = Policy.Handle<SqlException>()
                        .WaitAndRetry(
                        retryCount: 5,
                        sleepDurationProvider: retryAttemp => TimeSpan.FromSeconds(Math.Pow(2, retryAttemp)),
                        onRetry: (exception, span, count) =>
                        {
                            logger.LogError($"Retrying because of {exception} {span}");
                        });
                    retry.Execute(() => CallSeeder(seeder, context, services));
                    logger.LogInformation($"Migration Completed: {typeof(TContext).Name}");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"An error occurred while migrating db: {typeof(TContext).Name}");
                }
            }
            return host;
        }

        private static void CallSeeder<TContext>(Action<TContext, IServiceProvider> seeder, TContext? context, IServiceProvider services) where TContext : DbContext
        {
            context.Database.Migrate(); //Equivalent to running: dotnet ef database update
            seeder(context, services);
            //Runs your seed logic AFTER migrations are applied
            //→ Only seeds if data doesn't already exist 
        }
    }
}