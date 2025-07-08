using Microsoft.SqlServer.TransactSql.ScriptDom;
using Newtonsoft.Json;

namespace TestApp
{
    internal class IgnoreTokenStreamConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return typeof(IList<TSqlParserToken>).IsAssignableFrom(objectType);
        }

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            writer.WriteNull();
        }
    }
}