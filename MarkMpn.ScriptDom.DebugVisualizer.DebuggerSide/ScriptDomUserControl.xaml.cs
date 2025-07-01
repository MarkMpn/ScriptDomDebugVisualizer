using ColorCode;
using ColorCode.Compilation.Languages;
using ColorCode.Styling;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.VisualStudio.Extensibility.DebuggerVisualizers;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;

namespace MarkMpn.ScriptDom.DebugVisualizer.DebuggerSide
{
    // https://github.com/Giorgi/EFCore.Visualizer/blob/main/src/EFCore.Visualizer/QueryPlanUserControl.xaml.cs
    public partial class ScriptDomUserControl : System.Windows.Controls.UserControl
    {
        private string? _filePath;
        private readonly VisualizerTarget _visualizerTarget;
        private readonly Color _backgroundColor = Color.Black;// VSColorTheme.GetThemedColor(ThemedDialogColors.WindowPanelBrushKey);
        private TSqlFragment _currentHighlight;
        private string _currentHighlightKey;
        private readonly SemaphoreSlim _highlightLock = new SemaphoreSlim(1, 1);
        private static readonly string AssemblyLocation = Path.GetDirectoryName(typeof(ScriptDomUserControl).Assembly.Location);

        public ScriptDomUserControl(VisualizerTarget visualizerTarget)
        {
            this._visualizerTarget = visualizerTarget;
            InitializeComponent();

            Unloaded += ScriptDomUserControlUnloaded;
        }

        private void ScriptDomUserControlUnloaded(object sender, RoutedEventArgs e)
        {
            SafeDeleteFile(_filePath);

            Unloaded -= ScriptDomUserControlUnloaded;
        }

#pragma warning disable VSTHRD100 // Avoid async void methods
        protected override async void OnInitialized(EventArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
            SafeDeleteFile(_filePath);

            try
            {
                base.OnInitialized(e);

                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(AssemblyLocation, "WVData"));
                await webView.EnsureCoreWebView2Async(environment);

                webView.CoreWebView2.Profile.PreferredColorScheme = IsBackgroundDarkColor(_backgroundColor) ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
#if !DEBUG
                webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
#endif
                var fragment = await GetFragmentAsync();
                new Sql160ScriptGenerator().GenerateScript(fragment, out var sql);

                if (fragment is TSqlStatement)
                {
                    // Re-parse the statement to a new fragment so we can correlate each part of the DOM with the
                    // corresponding tokens
                    var parser = new TSql160Parser(false);
                    using var reader = new StringReader(sql);
                    fragment = ((TSqlScript)parser.Parse(reader, out _)).Batches.Single().Statements.Single();
                }

                var formatter = new HtmlFormatter(IsBackgroundDarkColor(_backgroundColor) ? StyleDictionary.DefaultDark : StyleDictionary.DefaultLight);
                var html = formatter.GetHtmlString(sql, Languages.Sql);
                _filePath = Path.Combine(Path.GetTempPath(), Path.ChangeExtension(Path.GetRandomFileName(), "html"));

                using var templateStream = GetType().Assembly.GetManifestResourceStream("MarkMpn.ScriptDom.DebugVisualizer.DebuggerSide.template.html");
                using var templateReader = new StreamReader(templateStream);
                var template = templateReader.ReadToEnd();

                html = template.Replace("{{body}}", html);

                File.WriteAllText(_filePath, html);

                treeView.Items.Clear();
                treeView.Items.Add(CreateTreeViewItem(fragment, null));
                treeView.MouseLeave += UnHighlightFragment;
                treeView.MouseMove += HighlightFragment;
                treeView.SelectedItemChanged += HighlightFragmentOnClick;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Cannot retrieve script: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (!string.IsNullOrEmpty(_filePath))
                {
                    webView.CoreWebView2.Navigate(_filePath);
                }
            }
        }

        private void TreeView_MouseEnter(object sender, MouseEventArgs e)
        {
            throw new NotImplementedException();
        }

        private TreeViewItem CreateTreeViewItem(TSqlFragment fragment, string prefix)
        {
            var item = new TreeViewItem { Header = (prefix == null ? null : (prefix + " - ")) + fragment.GetType().Name };

            foreach (var prop in fragment.GetType().GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
            {
                var child = prop.GetValue(fragment);

                if (child == null)
                    continue;

                if (child is TSqlFragment childFragment)
                {
                    item.Items.Add(CreateTreeViewItem(childFragment, prop.Name));
                }
                else if (prop.PropertyType.IsGenericType && prop.PropertyType.GetGenericTypeDefinition() == typeof(IList<>) && typeof(TSqlFragment).IsAssignableFrom(prop.PropertyType.GetGenericArguments()[0]))
                {
                    var i = 0;

                    foreach (TSqlFragment childItemFragment in (System.Collections.IEnumerable)child)
                    {
                        item.Items.Add(CreateTreeViewItem(childItemFragment, $"{prop.Name}[{i}]"));
                        i++;
                    }
                }
            }

            item.IsExpanded = true;
            item.Tag = fragment;
            return item;
        }

        private async void HighlightFragment(object sender, MouseEventArgs e)
        {
            _ =HighlightFragment(e.Source as TreeViewItem);
        }

        private async Task HighlightFragment(TreeViewItem? item)
        {
            if (item == null)
                return;

            if (treeView.SelectedItem != null && treeView.SelectedItem != item)
                return;

            await _highlightLock.WaitAsync();

            try
            {
                var fragment = (TSqlFragment)item.Tag;

                if (_currentHighlight == fragment ||
                    _currentHighlight?.StartOffset == fragment.StartOffset && _currentHighlight?.FragmentLength == fragment.FragmentLength)
                    return;

                if (_currentHighlightKey != null)
                    await webView.CoreWebView2.ExecuteScriptAsync($"removeHighlight('{_currentHighlightKey}')");

                var json = await webView.CoreWebView2.ExecuteScriptAsync($"highlightFragment({fragment.StartOffset - CountPrefixCr(fragment)}, {fragment.FragmentLength - CountCr(fragment)})");
                var id = JValue.Parse(json).Value<string>();

                _currentHighlight = fragment;
                _currentHighlightKey = id;
            }
            finally
            {
                _highlightLock.Release();
            }
        }

        private void HighlightFragmentOnClick(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            _ = HighlightFragment(e.NewValue as TreeViewItem);
        }

        private int CountPrefixCr(TSqlFragment fragment)
        {
            var cr = 0;

            for (var i = 0; i < fragment.FirstTokenIndex; i++)
            {
                var token = fragment.ScriptTokenStream[i];

                for (var j = 0; j < token.Text.Length; j++)
                {
                    if (token.Text[j] == '\r')
                        cr++;
                }
            }

            return cr;
        }

        private int CountCr(TSqlFragment fragment)
        {
            var cr = 0;

            for (var i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
            {
                var token = fragment.ScriptTokenStream[i];

                for (var j = 0; j < token.Text.Length; j++)
                {
                    if (token.Text[j] == '\r')
                        cr++;
                }
            }

            return cr;
        }

        private async void UnHighlightFragment(object sender, MouseEventArgs e)
        {
            await _highlightLock.WaitAsync();

            try
            {
                if (_currentHighlightKey != null)
                    await webView.CoreWebView2.ExecuteScriptAsync($"removeHighlight('{_currentHighlightKey}')");

                _currentHighlight = null;
                _currentHighlightKey = null;
            }
            finally
            {
                _highlightLock.Release();
            }
        }

        private async Task<TSqlFragment> GetFragmentAsync()
        {
            if (_visualizerTarget == null)
            {
                var query = @"
SELECT name
FROM (
    SELECT TOP 10 *
    FROM account
) AS SubQuery
GROUP BY name";


                var parser = new TSql160Parser(false);
                using var reader = new StringReader(query);
                return ((TSqlScript)parser.Parse(reader, out _)).Batches.Single().Statements.Single();
            }

            var serializedFragment = await _visualizerTarget.ObjectSource.RequestDataAsync<SerializedFragment>(null, CancellationToken.None);
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto
            };
            return JsonConvert.DeserializeObject<TSqlFragment>(serializedFragment.Fragment, settings);
        }

        private static void SafeDeleteFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                File.Delete(path);
            }
            catch
            {
                // Ignore
            }
        }

        private static bool IsBackgroundDarkColor(Color color) => color.R * 0.2126 + color.G * 0.7152 + color.B * 0.0722 < 255 / 2.0;

        private void WebViewNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            //webView.CoreWebView2.AddHostObjectToScript("host", this);
            _ = webView.CoreWebView2.ExecuteScriptAsync($"document.querySelector(':root').style.setProperty('--bg-color', 'RGB({_backgroundColor.R}, {_backgroundColor.G}, {_backgroundColor.B})');");
        }
    }
}
