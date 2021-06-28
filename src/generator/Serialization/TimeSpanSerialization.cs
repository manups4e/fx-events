using Microsoft.CodeAnalysis;
using Moonlight.Generators.Syntax;

namespace Moonlight.Generators.Serialization
{
    public class TimeSpanSerialization : IDefaultSerialization
    {
        public void Serialize(SerializationEngine engine, IPropertySymbol property, ITypeSymbol type, CodeWriter code, string name,
            string typeIdentifier, Location location)
        {
            code.AppendLine($"writer.Write({name}.Ticks);");
        }

        public void Deserialize(SerializationEngine engine, IPropertySymbol property, ITypeSymbol type, CodeWriter code, string name,
            string typeIdentifier, Location location)
        {
            code.AppendLine($"{name} = new TimeSpan(reader.ReadInt64());");
        }
    }
}