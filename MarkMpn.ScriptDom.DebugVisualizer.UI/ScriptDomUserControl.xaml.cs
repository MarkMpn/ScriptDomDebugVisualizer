using ColorCode;
using ColorCode.Styling;
using HtmlAgilityPack;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;

namespace MarkMpn.ScriptDom.DebugVisualizer.UI
{
    // https://github.com/Giorgi/EFCore.Visualizer/blob/main/src/EFCore.Visualizer/QueryPlanUserControl.xaml.cs
    public partial class ScriptDomUserControl : System.Windows.Controls.UserControl, ISite
    {
        private string? _filePath;
        private bool _clicked;
        private readonly Func<Task<SerializedFragment>> _fragmentSource;
        private readonly Color _backgroundColor;
        private readonly SemaphoreSlim _highlightLock = new SemaphoreSlim(1, 1);
        private readonly System.Windows.Forms.PropertyGrid _propertyGrid;
        private static readonly string AssemblyLocation = Path.GetDirectoryName(typeof(ScriptDomUserControl).Assembly.Location);

        public ScriptDomUserControl(Func<Task<SerializedFragment>> fragmentSource, Color backgroundColor)
        {
            _fragmentSource = fragmentSource;
            _backgroundColor = backgroundColor;
            _propertyGrid = new System.Windows.Forms.PropertyGrid();
            _propertyGrid.Site = this;

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

                propertyGridHost.Child = _propertyGrid;

                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(AssemblyLocation, "WVData"));
                await webView.EnsureCoreWebView2Async(environment);

                webView.CoreWebView2.Profile.PreferredColorScheme = IsBackgroundDarkColor(_backgroundColor) ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
#if !DEBUG
                webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
#endif
                var serializedFragment = await _fragmentSource();
                var sql = serializedFragment.Sql;
                var fragment = new TSql170Parser(false).Parse(new StringReader(sql), out _);
                var fragmentType = typeof(TSqlFragment).Assembly.GetType(serializedFragment.FragmentType);

                if (fragmentType == typeof(TSqlBatch))
                    fragment = ((TSqlScript)fragment).Batches.Single();
                else if (typeof(TSqlStatement).IsAssignableFrom(fragmentType))
                    fragment = ((TSqlScript)fragment).Batches.Single().Statements.Single();

                // Generate the HTML for the script
                var formatter = new HtmlFormatter(IsBackgroundDarkColor(_backgroundColor) ? StyleDictionary.DefaultDark : StyleDictionary.DefaultLight);
                var html = formatter.GetHtmlString(sql, Languages.Sql);

                // Tag each token within the formatted HTML
                html = TagTokens(html, fragment);

                _filePath = Path.Combine(Path.GetTempPath(), Path.ChangeExtension(Path.GetRandomFileName(), "html"));

                using var templateStream = GetType().Assembly.GetManifestResourceStream("MarkMpn.ScriptDom.DebugVisualizer.UI.template.html");
                using var templateReader = new StreamReader(templateStream);
                var template = templateReader.ReadToEnd();

                html = template.Replace("{{body}}", html);

                File.WriteAllText(_filePath, html);

                treeView.Items.Clear();
                treeView.Items.Add(CreateTreeViewItem(fragment, null));
                treeView.MouseLeave += UnHighlightFragment;
                treeView.MouseMove += HighlightFragmentOnTreeViewMouseMove;
                treeView.SelectedItemChanged += HighlightFragmentOnClick;
                treeView.PreviewMouseDown += HighlightFragmentOnTreeViewClick;

                statusBar.MouseDown += HighlightFragmentOnStatusBarClick;
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

        private void HighlightFragmentOnTreeViewClick(object sender, MouseButtonEventArgs e)
        {
            if (e.Source is TreeViewItem item)
            {
                _ = HighlightFragment(item);
                _clicked = true;
            }
            else
            {
                UnHighlightFragment(sender, e);
                _clicked = false;
            }
        }

        private void HighlightFragmentOnStatusBarClick(object sender, MouseButtonEventArgs e)
        {
            if (e.Source is StatusBarItem item && item.Tag is TreeViewItem treeViewItem)
            {
                treeViewItem.IsSelected = true;
                treeViewItem.BringIntoView();
            }
        }

        private string TagTokens(string html, TSqlFragment fragment)
        {
            if (fragment.ScriptTokenStream == null)
                return html;

            var parsed = new HtmlDocument();
            parsed.LoadHtml(html);

            var node = FindNextTextNode(parsed.DocumentNode);

            var tokenIndex = 0;

            // Skip any leading whitespace
            while (tokenIndex < fragment.ScriptTokenStream.Count)
            {
                if (fragment.ScriptTokenStream[tokenIndex].TokenType == TSqlTokenType.WhiteSpace)
                    tokenIndex++;
                else
                    break;
            }

            var taggedLength = 0;

            for (; tokenIndex < fragment.ScriptTokenStream.Count; tokenIndex++)
            {
                var token = fragment.ScriptTokenStream[tokenIndex];

                if (token.TokenType == TSqlTokenType.EndOfFile)
                    break;

                node = FindNextTextNode(node);
                var text = HttpUtility.HtmlDecode(node.Text);

                if (text.Length > token.Text.Length)
                {
                    // Split this node into two parts
                    var suffixNode = parsed.CreateTextNode(HttpUtility.HtmlEncode(text.Substring(token.Text.Length)));
                    node.Text = HttpUtility.HtmlEncode(text.Substring(0, token.Text.Length));
                    node.ParentNode.InsertAfter(suffixNode, node);
                }
                else if (text.Length < token.Text.Length)
                {
                    // This token spans multiple text nodes. Tag this one and continue to the next
                    taggedLength += text.Length;
                }

                if (taggedLength == 0)
                    Debug.Assert(HttpUtility.HtmlDecode(node.Text) == token.Text);
                else
                    Debug.Assert(HttpUtility.HtmlDecode(node.Text) == token.Text.Substring(taggedLength - text.Length, text.Length));

                // Tag the node so we can highlight it later
                var span = parsed.CreateElement("span");
                node.ParentNode.ReplaceChild(span, node);
                span.AppendChild(node);
                span.AddClass("token");
                span.SetAttributeValue("data-token-index", tokenIndex.ToString());

                if (taggedLength > 0)
                {
                    if (taggedLength == token.Text.Length)
                        taggedLength = 0;
                    else
                        tokenIndex--;
                }
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

        private async void HighlightFragmentOnTreeViewMouseMove(object sender, MouseEventArgs e)
        {
            if (_clicked)
                return;

            if (e.Source is TreeViewItem item)
                item.IsSelected = true;
        }

        public async Task SelectFragment(TSqlFragment fragment)
        {
            var treeViewItem = FindFragment((TreeViewItem)treeView.Items[0], fragment);

            if (treeViewItem != null)
                treeViewItem.IsSelected = true;
        }

        private TreeViewItem? FindFragment(TreeViewItem treeViewItem, TSqlFragment fragment)
        {
            if (treeViewItem.Tag == fragment)
                return treeViewItem;

            foreach (TreeViewItem child in treeViewItem.Items)
            {
                var foundItem = FindFragment(child, fragment);
                if (foundItem != null)
                    return foundItem;
            }

            return null;
        }

        private async Task HighlightFragment(TreeViewItem? item)
        {
            if (item == null)
                return;

            var fragment = (TSqlFragment)item.Tag;
            _propertyGrid.SelectedObject = new ScriptDomTypeDescriptor(fragment);

            await _highlightLock.WaitAsync();

            try
            {
                statusBar.Items.Clear();
                AddStatusBarItems(item);

                await webView.CoreWebView2.ExecuteScriptAsync($"highlightFragment({fragment.FirstTokenIndex}, {fragment.LastTokenIndex})");
            }
            finally
            {
                _highlightLock.Release();
            }
        }

        private void AddStatusBarItems(TreeViewItem item)
        {
            var text = (string)item.Header;

            if (text.EndsWith(" - " + item.Tag.GetType().Name))
                text = text.Substring(0, text.Length - 3 - item.Tag.GetType().Name.Length);

            if (statusBar.Items.Count > 0)
            {
                var separator = new StatusBarItem
                {
                    Content = ">"
                };
                statusBar.Items.Insert(0, separator);
            }

            var statusBarItem = new StatusBarItem
            {
                Content = text,
                Tag = item,
            };

            statusBar.Items.Insert(0, statusBarItem);

            if (item.Parent is TreeViewItem parentItem)
                AddStatusBarItems(parentItem);
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
                statusBar.Items.Clear();
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

        IComponent ISite.Component => null;

        IContainer ISite.Container => null;

        bool ISite.DesignMode => false;

        string ISite.Name { get; set; }

        object IServiceProvider.GetService(Type serviceType)
        {
            if (serviceType == GetType())
                return this;

            return null;
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
