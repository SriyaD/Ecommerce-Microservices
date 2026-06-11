using Catalog.Entities;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Search;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection.Metadata;
using System.Security.Cryptography.Xml;
using System.Text.Json;
using static MongoDB.Bson.Serialization.Serializers.SerializerHelper;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Catalog.Data
{
    public class DatabaseSeeder
    {
        //This is a one-time data initializer — it checks if collections are empty and seeds them from JSON files.Think of it as EF Core's HasData() seeding, but for MongoDB.
        //        App Starts
        //    │
        //    ▼
        //SeedAsync() called
        //    │
        //    ├──→ Brands empty?  → read brands.json  → InsertMany
        //    ├──→ Types empty?   → read types.json   → InsertMany
        //    └──→ Products empty?→ read products.json→ InsertMany

        //    SeedAsync()
        //│
        //├── Connect to MongoDB(MongoClient → GetDatabase → GetCollection x3)
        //│
        //├── BRANDS
        //│     COUNT(*) == 0? ──Yes──→ Read brands.json → Deserialize → InsertMany
        //│                    ──No───→ Find all → load into brandList
        //│
        //├── TYPES
        //│     COUNT(*) == 0? ──Yes──→ Read types.json → Deserialize → InsertMany
        //│                    ──No───→ Find all → load into typeList
        //│
        //└── PRODUCTS
        //      COUNT(*) == 0? ──Yes──→ Read products.json → Deserialize
        //                              → foreach: Id= null, CreatedDate= UtcNow
        //                              → InsertMany
        //                     ──No───→ Skip (products already seeded)
        public static async Task SeedAsync(IConfiguration configuration)
        {
            //Opens connection to MongoDB server
            var client = new MongoClient(configuration["DatabaseSettings:ConnectionString"]);
            var db = client.GetDatabase(configuration["DatabaseSettings:DatabaseName"]);
            var products = db.GetCollection<Product>(configuration["DatabaseSettings:ProductCollectionName"]);
            var brands = db.GetCollection<ProductBrand>(configuration["DatabaseSettings:BrandCollectionName"]);
            var types = db.GetCollection<ProductType>(configuration["DatabaseSettings:TypeCollectionName"]);

            var SeedBasePath = Path.Combine("Data", "SeedData");

            //Seed Brands
            List<ProductBrand> brandList = new();
            // _ => true   // lambda that ignores the document and always returns true
            // = match ALL documents = COUNT(*)
            //If count is 0 → collection is empty → seed it.

            //            InsertManyAsync
            //Bulk insert — far more efficient than inserting one by one in a loop.
            //brands.json → deserialize → List < ProductBrand > → InsertManyAsync → MongoDB
            if ((await brands.CountDocumentsAsync(_ => true)) == 0)
            {
                var brandData = await File.ReadAllTextAsync(Path.Combine(SeedBasePath, "brands.json"));
                brandList = JsonSerializer.Deserialize<List<ProductBrand>>(brandData);
                await brands.InsertManyAsync(brandList);
            }
            else
            {
                brandList = await brands.Find(_ => true).ToListAsync();
            }

            //Seed Types
            List<ProductType> typeList = new();
            if ((await types.CountDocumentsAsync(_ => true)) == 0)
            {
                var typeData = await File.ReadAllTextAsync(Path.Combine(SeedBasePath, "types.json"));
                typeList = JsonSerializer.Deserialize<List<ProductType>>(typeData);
                await types.InsertManyAsync(typeList);
            }
            else
            {
                typeList = await types.Find(_ => true).ToListAsync();
            }





            //Seed Products
            //            ① product.Id = null — Why?
            //csharpproduct.Id = null;
            //            Your JSON file may have hardcoded Ids like "id": "abc123".If you insert with a fixed Id:

            //Re - seeding on a fresh DB could conflict with existing Ids
            //The Id format may not be a valid MongoDB ObjectId

            //By setting Id = null, you tell MongoDB: "generate a new ObjectId for this document" — safe, unique, always valid.
            //② CreatedDate default guard
            //csharpif(product.CreatedDate == default)
            //    product.CreatedDate = DateTime.UtcNow;
            //            default for DateTime is DateTime.MinValue(Jan 1, 0001) — clearly invalid.
            //            If the JSON doesn't have a createdDate field, this ensures it gets a sensible timestamp instead of being stored as year 0001.
            

            //            Seeding Order Matters
            //            1.Brands  ──┐
            //             ├──→ Products reference both
            //            2.Types   ──┘    (Brand.Id, Type.Id inside Product document)

            //                3.Products(last)
            //Brands and types must exist before products are seeded because product documents embed brand/ type references.



            if ((await products.CountDocumentsAsync(_ => true)) == 0)
            {
                var productData = await File.ReadAllTextAsync(Path.Combine(SeedBasePath, "products.json"));
                var productList = JsonSerializer.Deserialize<List<Product>>(productData);
                foreach (var product in productList)
                {
                    //Reset Id to let Mongo generate one
                    product.Id = null;
                    //Default CreatedDate if not set
                    if (product.CreatedDate == default)
                        product.CreatedDate = DateTime.UtcNow;
                }
                await products.InsertManyAsync(productList);
            }
        }
    }
}
