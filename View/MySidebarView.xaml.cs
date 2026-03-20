using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using GsPlugin.Api;
using GsPlugin.Infrastructure;
using GsPlugin.Models;
using Microsoft.Web.WebView2.Core;

namespace GsPlugin.View {
    public partial class MySidebarView : UserControl {

        private readonly IGsApiClient _apiClient;
        private bool _webView2Ready;
        private DateTime _lastNavigatedAtUtc = DateTime.MinValue;

        public MySidebarView(IGsApiClient apiClient) {
            InitializeComponent();
            _apiClient = apiClient;

            // One approach is to wait until the control is actually loaded in the visual tree.
            this.Loaded += MySidebarView_Loaded;
            this.IsVisibleChanged += MySidebarView_IsVisibleChanged;
        }

        private async void MySidebarView_Loaded(object sender, RoutedEventArgs e) {
            try {
                // Ensure the CoreWebView2 is ready to receive commands
                await MyWebView2.EnsureCoreWebView2Async();

                if (MyWebView2?.CoreWebView2 == null) {
                    GsLogger.Error("WebView2 initialization failed: CoreWebView2 is null after initialization");
                    ShowErrorMessage("Failed to load Game Scrobbler dashboard. WebView2 runtime may not be installed.");
                    return;
                }

                // Restrict navigation to gamescrobbler.com domains only
                MyWebView2.CoreWebView2.NavigationStarting += (s, args) => {
                    if (args.Uri != null) {
                        try {
                            var uri = new Uri(args.Uri);
                            if (uri.Host != "gamescrobbler.com" && !uri.Host.EndsWith(".gamescrobbler.com")) {
                                args.Cancel = true;
                                // Only open https links in the system browser
                                if (uri.Scheme == "https") {
                                    Process.Start(new ProcessStartInfo(args.Uri) { UseShellExecute = true });
                                }
                            }
                        }
                        catch (UriFormatException) {
                            args.Cancel = true;
                        }
                    }
                };

                MyWebView2.CoreWebView2.NewWindowRequested += (s, args) => {
                    args.Handled = true;
                    try {
                        var uri = new Uri(args.Uri);
                        // Only open https links in the system browser
                        if (uri.Scheme == "https") {
                            Process.Start(new ProcessStartInfo(args.Uri) { UseShellExecute = true });
                        }
                    }
                    catch (Exception ex) {
                        GsLogger.Warn($"Failed to open new window URL in browser: {ex.Message}");
                    }
                };

                // Listen for messages from the frontend (e.g. "gs:refresh-token" when
                // the dashboard session expires and the user clicks Retry).
                MyWebView2.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                _webView2Ready = true;
                await NavigateToDashboard();
            }
            catch (Exception ex) {
                GsLogger.Error("Failed to initialize sidebar WebView2", ex);
                GsSentry.CaptureException(ex, "Failed to initialize sidebar WebView2");
                ShowErrorMessage("Failed to load Game Scrobbler dashboard. Please check that WebView2 runtime is installed.");
            }
        }

        /// <summary>
        /// When the sidebar becomes visible again after being hidden, re-navigate
        /// with a fresh dashboard token if the previous one has likely expired
        /// (dashboard tokens have a 10-minute TTL).
        /// </summary>
        private async void MySidebarView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) {
            if ((bool)e.NewValue && _webView2Ready) {
                if ((DateTime.UtcNow - _lastNavigatedAtUtc).TotalMinutes > 8) {
                    GsLogger.Info("Sidebar became visible after token likely expired — refreshing dashboard");
                    await NavigateToDashboard();
                }
            }
        }

        /// <summary>
        /// Handles postMessage calls from the frontend. The dashboard sends
        /// "gs:refresh-token" when the session has expired and the user clicks Retry,
        /// so the plugin can fetch a fresh dashboard token and re-navigate.
        /// </summary>
        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e) {
            try {
                string message = e.TryGetWebMessageAsString();
                if (message == "gs:refresh-token") {
                    GsLogger.Info("Received refresh-token request from dashboard");
                    await NavigateToDashboard();
                }
            }
            catch (Exception ex) {
                GsLogger.Warn($"Failed to process web message: {ex.Message}");
            }
        }

        /// <summary>
        /// Fetches a fresh dashboard token (or falls back to user_id for legacy installs)
        /// and navigates the WebView2 to the dashboard URL.
        /// Only theme is passed as a URL param (cosmetic, needed for instant rendering).
        /// All other context (plugin_version, flags, preferences) is sent server-side
        /// via the POST /v2/dashboard-token body and returned to the frontend when
        /// the token is resolved — tamper-proof.
        /// </summary>
        private async System.Threading.Tasks.Task NavigateToDashboard() {
            try {
                string theme = Uri.EscapeDataString((GsDataManager.Data.Theme ?? "Dark").ToLower());

                string url;
                bool hasInstallToken = !string.IsNullOrEmpty(GsDataManager.DataOrNull?.InstallToken);

                if (hasInstallToken) {
                    // Install is registered — request a short-lived dashboard token to keep
                    // the install UUID out of the browser URL and history.
                    // The POST body includes dashboard context (plugin_version, flags, etc.)
                    // which the server stores alongside the token.
                    var dashboardToken = _apiClient != null
                        ? await _apiClient.GetDashboardToken()
                        : null;

                    if (!string.IsNullOrEmpty(dashboardToken)) {
                        url = $"https://gamescrobbler.com/dashboard/playnite?access_token={Uri.EscapeDataString(dashboardToken)}&theme={theme}";
                        GsLogger.Info("Dashboard URL built with access_token (install UUID not in URL)");
                    }
                    else {
                        // Dashboard-token request failed (network/server error) — fail closed
                        // rather than falling back to the user_id URL which would leak the
                        // install UUID into browser history for already-registered installs.
                        GsLogger.Error("GetDashboardToken failed for a registered install; aborting dashboard navigation");
                        ShowErrorMessage("Failed to load Game Scrobbler dashboard. Please try again later.");
                        return;
                    }
                }
                else {
                    // No install token yet — use bare install ID (pre-registration behaviour).
                    // Context flags are only sent via the token POST body once registered;
                    // unregistered installs use server defaults until token registration completes.
                    string userId = Uri.EscapeDataString(GsDataManager.Data.InstallID);
                    url = $"https://gamescrobbler.com/dashboard/playnite?user_id={userId}&theme={theme}";
                    GsLogger.Warn("Dashboard URL built with user_id fallback (install not yet registered)");
                }

                _lastNavigatedAtUtc = DateTime.UtcNow;
                MyWebView2.CoreWebView2.Navigate(url);
            }
            catch (Exception ex) {
                GsLogger.Error("Failed to navigate to dashboard", ex);
                GsSentry.CaptureException(ex, "Failed to navigate to dashboard");
                ShowErrorMessage("Failed to load Game Scrobbler dashboard. Please try again later.");
            }
        }

        private void ShowErrorMessage(string message) {
            try {
                var grid = (Grid)Content;
                grid.Children.Clear();
                grid.Children.Add(new TextBlock {
                    Text = message,
                    Foreground = System.Windows.Media.Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(20),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }
            catch {
                // Silently fail if we can't show the error UI
            }
        }
    }
}
