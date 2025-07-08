using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MarkMpn.ScriptDom.DebugVisualizer.UI;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Newtonsoft.Json;
using SerializedFragment = MarkMpn.ScriptDom.DebugVisualizer.UI.SerializedFragment;

namespace TestApp
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            AddChild(new ScriptDomUserControl(() => GetTestFragmentAsync(), Color.White));
        }

        private Task<SerializedFragment> GetTestFragmentAsync()
        {
            var query = @"
SELECT name
FROM (
    SELECT TOP 10 *
    FROM account
) AS SubQuery
GROUP BY name";


            var parser = new TSql160Parser(false);
            using (var reader = new StringReader(query))
            {
                var fragment = (TSqlFragment)((TSqlScript)parser.Parse(reader, out _)).Batches.Single().Statements.Single();

                var settings = new JsonSerializerSettings
                {
                    TypeNameHandling = TypeNameHandling.Auto,
                    Converters = new List<JsonConverter>
                    {
                        new IgnoreTokenStreamConverter()
                    }
                };
                var json = JsonConvert.SerializeObject(fragment, typeof(TSqlFragment), settings);

                return Task.FromResult(new SerializedFragment
                {
                    Fragment = json,
                    Sql = query
                });
            }
        }
    }
}
