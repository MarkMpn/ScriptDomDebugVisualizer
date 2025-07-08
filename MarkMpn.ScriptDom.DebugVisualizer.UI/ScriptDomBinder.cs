using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Newtonsoft.Json.Serialization;

namespace MarkMpn.ScriptDom.DebugVisualizer.UI
{
    internal class ScriptDomBinder : ISerializationBinder
    {
        public void BindToName(Type serializedType, out string? assemblyName, out string? typeName)
        {
            throw new NotImplementedException();
        }

        public Type BindToType(string? assemblyName, string typeName)
        {
            if (assemblyName != "Microsoft.SqlServer.TransactSql.ScriptDom")
                return null;

            return typeof(TSqlFragment).Assembly.GetType(typeName);
        }
    }
}
