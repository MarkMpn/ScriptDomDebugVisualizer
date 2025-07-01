using ColorCode;
using ColorCode.Styling;
using HtmlAgilityPack;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.VisualStudio.Extensibility.DebuggerVisualizers;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
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

                // Generate the HTML for the script
                var formatter = new HtmlFormatter(IsBackgroundDarkColor(_backgroundColor) ? StyleDictionary.DefaultDark : StyleDictionary.DefaultLight);
                var html = formatter.GetHtmlString(sql, Languages.Sql);

                // Tag each token within the formatted HTML
                html = TagTokens(html, fragment);

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

        private string TagTokens(string html, TSqlFragment fragment)
        {
            if (fragment.ScriptTokenStream == null)
                return html;

            var parsed = new HtmlDocument();
            parsed.LoadHtml(html);

            var node = FindNextTextNode(parsed.DocumentNode);

            for (var tokenIndex = 0; tokenIndex < fragment.ScriptTokenStream.Count; tokenIndex++)
            {
                var token = fragment.ScriptTokenStream[tokenIndex];

                if (token.TokenType == TSqlTokenType.EndOfFile)
                    break;

                node = FindNextTextNode(node);

                if (node.Text.Length > token.Text.Length)
                {
                    // Split this node into two parts
                    var suffixNode = parsed.CreateTextNode(node.Text.Substring(token.Text.Length));
                    node.Text = node.Text.Substring(0, token.Text.Length);
                    node.ParentNode.InsertAfter(suffixNode, node);
                }

                Debug.Assert(node.Text == token.Text);

                // Tag the node so we can highlight it later
                var span = parsed.CreateElement("span");
                node.ParentNode.ReplaceChild(span, node);
                span.AppendChild(node);
                span.AddClass("token");
                span.SetAttributeValue("data-token-index", tokenIndex.ToString());
            }

            return parsed.DocumentNode.InnerHtml;
        }

        private HtmlTextNode? FindNextTextNode(HtmlNode node)
        {
            while (node != null)
            {
                if (node.FirstChild != null)
                {
                    node = node.FirstChild;
                }
                else if (node.NextSibling != null)
                {
                    node = node.NextSibling;
                }
                else
                {
                    while (node != null)
                    {
                        node = node.ParentNode;

                        if (node.NextSibling != null)
                        {
                            node = node.NextSibling;
                            break;
                        }
                    }
                }

                if (node is HtmlTextNode text)
                    return text;
            }

            return null;
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
            _ = HighlightFragment(e.Source as TreeViewItem);
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

                await webView.CoreWebView2.ExecuteScriptAsync($"highlightFragment({fragment.FirstTokenIndex}, {fragment.LastTokenIndex})");
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

        private async void UnHighlightFragment(object sender, MouseEventArgs e)
        {
            if (treeView.SelectedItem != null)
                return;

            await _highlightLock.WaitAsync();

            try
            {
                await webView.CoreWebView2.ExecuteScriptAsync($"highlightFragment()");
            }
            finally
            {
                _highlightLock.Release();
            }
        }

        public void HighlightToken(int tokenIndex)
        {
            // Called from JavaScript to highlight the token in the treeview that the user hovered over in the script view
            FindFragment((TreeViewItem)treeView.Items[0], tokenIndex);
        }

        private bool FindFragment(TreeViewItem item, int tokenIndex)
        {
            if (!(item.Tag is TSqlFragment fragment))
                return false;

            if (fragment.FirstTokenIndex > tokenIndex || fragment.LastTokenIndex < tokenIndex)
            {
                // This fragment doesn't contain the token we're looking for
                return false;
            }

            // Check if there's a more specific child that also contains this token
            foreach (TreeViewItem child in item.Items)
            {
                if (FindFragment(child, tokenIndex))
                {
                    // Found a child that contains the token, so select it
                    return true;
                }
            }

            // This is the most specific fragment, so select it
            item.IsSelected = true;
            item.BringIntoView();
            return true;
        }

        private async Task<TSqlFragment> GetFragmentAsync()
        {
#if DEBUG
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
#endif

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
            webView.CoreWebView2.AddHostObjectToScript("host", new HostObject(this));
            _ = webView.CoreWebView2.ExecuteScriptAsync($"document.querySelector(':root').style.setProperty('--bg-color', 'RGB({_backgroundColor.R}, {_backgroundColor.G}, {_backgroundColor.B})');");
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.AutoDual)]
        public class HostObject
        {
            private readonly ScriptDomUserControl _control;

            public HostObject(ScriptDomUserControl control)
            {
                _control = control ?? throw new ArgumentNullException(nameof(control));
            }

            public void HighlightToken(int tokenIndex)
            {
                _control.HighlightToken(tokenIndex);
            }
        }
    }
}
