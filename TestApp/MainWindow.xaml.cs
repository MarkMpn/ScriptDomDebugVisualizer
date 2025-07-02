using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MarkMpn.ScriptDom.DebugVisualizer.DebuggerSide;
using Microsoft.SqlServer.TransactSql.ScriptDom;

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

            AddChild(new ScriptDomUserControl(() => GetTestFragmentAsync()));
        }

        private Task<TSqlFragment> GetTestFragmentAsync()
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
                return Task.FromResult((TSqlFragment)((TSqlScript)parser.Parse(reader, out _)).Batches.Single().Statements.Single());
            }
        }
    }
}
