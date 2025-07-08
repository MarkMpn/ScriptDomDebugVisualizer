using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.VisualStudio.DebuggerVisualizers;
using Newtonsoft.Json;

namespace MarkMpn.ScriptDom.DebugVisualizer.DebugeeSide
{
    public class ScriptDomObjectSource : VisualizerObjectSource
    {
        public override void GetData(object target, Stream outgoingData)
        {
            var fragment = (TSqlFragment)target;

            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                Converters = new List<JsonConverter>
                {
                    new IgnoreTokenStreamConverter()
                }
            };
            var json = JsonConvert.SerializeObject(target, typeof(TSqlFragment), settings);
            var serialized = new SerializedFragment { Fragment = json };

            if (fragment.ScriptTokenStream != null)
                serialized.Sql = string.Join("", fragment.ScriptTokenStream.Skip(fragment.FirstTokenIndex).Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1).Select(t => t.Text));

            SerializeAsJson(outgoingData, serialized);
        }
    }
}
