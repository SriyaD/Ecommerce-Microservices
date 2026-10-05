using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;

namespace Catalog.Entities
{
    public class BaseEntity
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; }
    }
}



//[BsonId] (from MongoDB.Bson.Serialization.Attributes)
//- Tells the MongoDB C# driver to map this property to the document's _id field.
//- MongoDB requires every document to have an _id. Without this attribute, the driver would still pick up a property named Id by convention, but the attribute makes the mapping explicit.

//[BsonRepresentation(BsonType.ObjectId)]
//- In the database, _id is stored as a native MongoDB ObjectId, a 12-byte binary value such as ObjectId("65a1f3c2e4b0a1b2c3d4e5f6").
//- In C#, it appears as a plain string ("65a1f3c2e4b0a1b2c3d4e5f6").
//- The driver converts between the two on every read and write.[BsonId] (from MongoDB.Bson.Serialization.Attributes)
//- Tells the MongoDB C# driver to map this property to the document's _id field.
//- MongoDB requires every document to have an _id. Without this attribute, the driver would still pick up a property named Id by convention, but the attribute makes the mapping explicit.
