using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.VisualStudio.DebuggerVisualizers;

namespace MarkMpn.ScriptDom.DebugVisualizer.DebugeeSide
{
    public class ScriptDomObjectSource : VisualizerObjectSource
    {
        public override void GetData(object target, Stream outgoingData)
        {
            var serialized = new SerializedFragment { FragmentType = target?.GetType().FullName };

            if (target is TSqlFragment fragment)
            {
                if (fragment.ScriptTokenStream != null)
                {
                    serialized.Sql = string.Join("", fragment.ScriptTokenStream.Skip(fragment.FirstTokenIndex).Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1).Select(t => t.Text));
                }
                else
                {
                    new Sql170ScriptGenerator().GenerateScript(fragment, out var sql);
                    serialized.Sql = sql;
                }
            }
            else
            {
                serialized.Sql = target?.ToString() ?? string.Empty;
            }

            SerializeAsJson(outgoingData, serialized);
        }
    }
}
