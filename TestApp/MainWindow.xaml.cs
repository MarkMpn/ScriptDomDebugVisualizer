using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MarkMpn.ScriptDom.DebugVisualizer.UI;
using Microsoft.SqlServer.TransactSql.ScriptDom;
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

            AddChild(new ScriptDomUserControl(() => GetTestFragmentAsync(), Color.Black));
        }

        private Task<SerializedFragment> GetTestFragmentAsync()
        {
            var query = @"
SELECT name, N'Unicode test'
FROM (
    SELECT TOP 10 *
    FROM account
) AS SubQuery
GROUP BY name";

            return Task.FromResult(new SerializedFragment
            {
                Sql = query,
                FragmentType = typeof(SelectStatement).FullName
            });
        }
    }
}
