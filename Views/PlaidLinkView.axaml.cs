using System;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Avalonia.Media;
using MyApp.ViewModels;
using System.Diagnostics;
using ReactiveUI;
using Avalonia.ReactiveUI;
// WebView implementation
using WebView.Avalonia; 
using WebViewCore = WebView.Avalonia.WebView;

namespace MyApp.Views
{
    public partial class PlaidLinkView : ReactiveUserControl<PlaidLinkViewModel>
    {
        // Use the inherited ViewModel property from ReactiveUserControl<PlaidLinkViewModel>
        private readonly Panel _webViewContainer;
        
        // Placeholder for a WebView implementation
        private Control _webView;
        
        // Flag to prevent duplicate WebView creation
        private bool _isWebViewInitialized;

        public PlaidLinkView()
        {
            InitializeComponent();
            
            _webViewContainer = this.FindControl<Panel>("WebViewContainer");
            _isWebViewInitialized = false;
            
            // Subscribe to data context changes to update the WebView
            this.GetObservable(DataContextProperty).Subscribe(OnDataContextChanged);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void OnDataContextChanged(object dataContext)
        {
            if (dataContext is PlaidLinkViewModel viewModel)
            {
                // Subscribe to changes in LinkUrl to update the WebView
                viewModel.WhenAnyValue(vm => vm.LinkUrl)
                    .Subscribe(url => UpdateWebViewUrl(url));
                
                // Subscribe to changes in IsWebViewVisible to show/hide the WebView
                viewModel.WhenAnyValue(vm => vm.IsWebViewVisible)
                    .Subscribe(isVisible => UpdateWebViewVisibility(isVisible));
            }
        }

        private void UpdateWebViewUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return;

            // Create a WebView if it doesn't exist
            if (_webView == null || !_isWebViewInitialized)
            {
                CreateWebView();
                _isWebViewInitialized = true;
            }
            
            Debug.WriteLine($"Navigating WebView to: {url}");
            
            // Navigate the WebView to the URL
            if (_webView is WebViewCore webView)
            {
                webView.Address = url;
                Debug.WriteLine("Set WebView address to Plaid Link URL");
            }
            else
            {
                Debug.WriteLine("Warning: WebView is not properly initialized");
                
                // In debug mode, allow fallback to placeholder for testing
                #if DEBUG
                if (_webView is Border border && border.Child is TextBlock textBlock)
                {
                    // Display debug info in development environment
                    var linkUri = new Uri(url);
                    string token = "token-not-found";
                    if (url.Contains("token="))
                    {
                        int tokenStart = url.IndexOf("token=") + 6;
                        int tokenEnd = url.IndexOf("&", tokenStart);
                        if (tokenEnd == -1) tokenEnd = url.Length;
                        token = url.Substring(tokenStart, tokenEnd - tokenStart);
                    }
                    
                    textBlock.Text = $"WebView Fallback (Debug Only)\nConnecting to: {url}\n\n" +
                                    $"Debug Info:\n" +
                                    $"Token: {(token.Length > 10 ? token.Substring(0, 10) + "..." : token)}\n" +
                                    $"URL Host: {linkUri.Host}\n" +
                                    $"URL Path: {linkUri.AbsolutePath}";
                }
                #endif
            }
        }

        private void UpdateWebViewVisibility(bool isVisible)
        {
            if (_webView != null)
            {
                _webView.IsVisible = isVisible;
            }
        }

        private void CreateWebView()
        {
            try
            {
                Debug.WriteLine("Creating WebView for Plaid Link...");
                
                // Create a real WebView control
                var webView = new WebViewCore
                {
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
                };
                
                // Subscribe to WebView events
                webView.MessageReceived += (_, message) => 
                {
                    HandlePlaidCallbackMessage(message);
                };
                
                // Set up JavaScript message handling
                string injectedScript = @"
                    window.addEventListener('message', function(event) {
                        if (event.data.action === 'plaid_link_message') {
                            // Send the message back to our app
                            window.chrome.webview.postMessage(JSON.stringify(event.data.message));
                        }
                    });
                    
                    // Override Plaid's onSuccess, onExit, and onEvent callbacks
                    const originalOnLoad = window.onload;
                    window.onload = function() {
                        if (originalOnLoad) originalOnLoad();
                        
                        // Wait for Plaid Link to be available
                        const checkPlaidLinkReady = setInterval(function() {
                            if (window.Plaid) {
                                clearInterval(checkPlaidLinkReady);
                                
                                // Save original handlers
                                const originalHandlers = {
                                    onSuccess: window.Plaid.onSuccess,
                                    onExit: window.Plaid.onExit,
                                    onEvent: window.Plaid.onEvent
                                };
                                
                                // Override onSuccess
                                window.Plaid.onSuccess = function(public_token, metadata) {
                                    // Call original handler if it exists
                                    if (originalHandlers.onSuccess) {
                                        originalHandlers.onSuccess(public_token, metadata);
                                    }
                                    
                                    // Forward to our app
                                    window.chrome.webview.postMessage(JSON.stringify({
                                        event: 'SUCCESS',
                                        metadata: {
                                            public_token: public_token,
                                            institution: metadata.institution
                                        }
                                    }));
                                };
                                
                                // Override onExit
                                window.Plaid.onExit = function(error, metadata) {
                                    // Call original handler if it exists
                                    if (originalHandlers.onExit) {
                                        originalHandlers.onExit(error, metadata);
                                    }
                                    
                                    // Forward to our app
                                    window.chrome.webview.postMessage(JSON.stringify({
                                        event: 'EXIT',
                                        metadata: {
                                            error: error,
                                            error_code: metadata ? metadata.error_code : ''
                                        }
                                    }));
                                };
                                
                                // Override onEvent
                                window.Plaid.onEvent = function(eventName, metadata) {
                                    // Call original handler if it exists
                                    if (originalHandlers.onEvent) {
                                        originalHandlers.onEvent(eventName, metadata);
                                    }
                                    
                                    // Forward to our app
                                    window.chrome.webview.postMessage(JSON.stringify({
                                        event: 'EVENT',
                                        metadata: {
                                            event_name: eventName,
                                            metadata: metadata
                                        }
                                    }));
                                };
                            }
                        }, 100);
                    };
                ";
                
                // Set up error handling
                webView.WebMessageReceived += (_, args) => 
                {
                    Debug.WriteLine($"WebView message: {args.WebMessageAsJson}");
                };
                
                webView.NavigationCompleted += (_, e) => 
                {
                    Debug.WriteLine($"WebView navigation completed: Success={e.IsSuccess}");
                    if (e.IsSuccess)
                    {
                        try
                        {
                            webView.ExecuteScriptAsync(injectedScript);
                            Debug.WriteLine("Injected Plaid callback script");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error injecting script: {ex.Message}");
                        }
                    }
                };
                
                // Add the WebView to the container
                _webView = webView;
                _webViewContainer.Children.Clear();
                _webViewContainer.Children.Add(_webView);
                
                Debug.WriteLine("WebView created successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating WebView: {ex.Message}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                
                // Fall back to the test implementation
                CreateFallbackWebView();
            }
        }

        // In a real implementation, you would have methods to handle WebView events
        // For example:
        
        // These methods would be implemented with a real WebView
        // For now, they are just placeholders
        
        private void SimulatePlaidLinkFlow()
        {
            // This method simulates the Plaid Link flow in a real WebView
            Debug.WriteLine("Simulating Plaid Link flow");
            
            // For development and testing, we can simulate the complete flow with events
            SimulateCompleteUserFlow();
        }
        
        /// <summary>
        /// Simulates a complete user flow through Plaid Link
        /// </summary>
        private void SimulateCompleteUserFlow()
        {
            // Detect if we're in sandbox mode first
            bool isSandbox = true; // Default to sandbox for simulation
            
            // Simulate the sequence of events that occur during a typical Plaid Link flow
            // This helps with testing the UI and callback handling without a real WebView
            
            // Event 1: Link opened
            SimulatePlaidEvent("EVENT", "OPEN", 0);
            
            // Event 2: User searches for institution
            SimulatePlaidEvent("EVENT", "SEARCH_INSTITUTION", 1000);
            
            // Event 3: User selects institution (use name that matches Plaid sandbox)
            string institutionName = isSandbox ? "First Platypus Bank" : "Mock Bank";
            string institutionId = isSandbox ? "ins_109508" : "ins_123";
            SimulatePlaidEvent("EVENT", "SELECT_INSTITUTION", institutionName, 2000);
            
            // Event 4: User submits credentials
            SimulatePlaidEvent("EVENT", "SUBMIT_CREDENTIALS", 3000);
            
            // Event 5: User views data before sharing
            SimulatePlaidEvent("EVENT", "VIEW_DATA", 4000);
            
            // Event 6: Success - user completes the flow with Plaid sandbox compatible values
            string publicToken = isSandbox ? "public-sandbox-123456789" : "mock_public_token_12345";
            var successEvent = $"{{\"event\":\"SUCCESS\",\"metadata\":{{\"public_token\":\"{publicToken}\",\"institution\":{{\"name\":\"{institutionName}\",\"institution_id\":\"{institutionId}\"}}}}}}";
            
            Debug.WriteLine($"Will simulate SUCCESS with public_token={publicToken}, institution={institutionName}");
            
            Task.Delay(5000).ContinueWith(_ => 
            {
                Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
                    HandlePlaidCallbackMessage(successEvent);
                });
            });
        }
        
        /// <summary>
        /// Simulates a Plaid event for testing
        /// </summary>
        private void SimulatePlaidEvent(string eventType, string eventName, int delayMs)
        {
            SimulatePlaidEvent(eventType, eventName, null, delayMs);
        }
        
        /// <summary>
        /// Simulates a Plaid event with additional data for testing
        /// </summary>
        private void SimulatePlaidEvent(string eventType, string eventName, string? additionalData, int delayMs)
        {
            string eventJson = string.Empty;
            
            switch (eventType)
            {
                case "EVENT":
                    if (eventName == "SELECT_INSTITUTION" && !string.IsNullOrEmpty(additionalData))
                    {
                        // For institution selection, include the bank name
                        eventJson = $"{{\"event\":\"EVENT\",\"metadata\":{{\"event_name\":\"{eventName}\",\"metadata\":{{\"institution_name\":\"{additionalData}\"}}}}}}";
                    }
                    else
                    {
                        // Generic event
                        eventJson = $"{{\"event\":\"EVENT\",\"metadata\":{{\"event_name\":\"{eventName}\",\"metadata\":{{}}}}}}";
                    }
                    break;
                    
                case "EXIT":
                    // Simulate user exit or error
                    if (!string.IsNullOrEmpty(additionalData))
                    {
                        // With error
                        eventJson = $"{{\"event\":\"EXIT\",\"metadata\":{{\"error\":\"{additionalData}\",\"error_code\":\"INVALID_CREDENTIALS\"}}}}";
                    }
                    else
                    {
                        // User cancelled
                        eventJson = "{\"event\":\"EXIT\",\"metadata\":{}}";
                    }
                    break;
                    
                case "ERROR":
                    // Explicit error event
                    eventJson = $"{{\"event\":\"ERROR\",\"metadata\":{{\"error\":\"{additionalData ?? "Unknown error"}\",\"error_code\":\"API_ERROR\"}}}}";
                    break;
            }
            
            if (!string.IsNullOrEmpty(eventJson))
            {
                // Simulate the event after the specified delay
                Task.Delay(delayMs).ContinueWith(_ => 
                {
                    Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
                        HandlePlaidCallbackMessage(eventJson);
                    });
                });
            }
        }
        
        /// <summary>
        /// Handles callback messages from the Plaid Link WebView
        /// </summary>
        /// <param name="message">JSON message from Plaid Link</param>
        
        /// <summary>
        /// Creates a fallback WebView for testing in case the real one fails
        /// </summary>
        private void CreateFallbackWebView()
        {
            Debug.WriteLine("Creating fallback WebView for testing");
            
            // Create a placeholder for the WebView
            _webView = new Border
            {
                Child = new TextBlock
                {
                    Text = "WebView Fallback (Test Mode)\nThis is a testing placeholder when WebView.Avalonia is not working",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                },
                Background = Avalonia.Media.Brushes.LightGray,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20)
            };
            
            // Add the WebView to the container
            _webViewContainer.Children.Add(_webView);
            
            // For testing, simulate Plaid Link flow
            SimulatePlaidLinkFlow();
        }
        
        private void HandlePlaidCallbackMessage(string message)
        {
            try
            {
                Debug.WriteLine($"Received Plaid callback: {message}");
                
                if (string.IsNullOrEmpty(message))
                {
                    Debug.WriteLine("Empty callback message received from Plaid");
                    return;
                }
                
                // Parse the JSON message
                using (JsonDocument doc = JsonDocument.Parse(message))
                {
                    var root = doc.RootElement;
                    
                    // Extract the event type
                    if (!root.TryGetProperty("event", out JsonElement eventElement))
                    {
                        Debug.WriteLine("Missing 'event' property in Plaid callback");
                        return;
                    }
                    
                    string eventType = eventElement.GetString() ?? string.Empty;
                    Debug.WriteLine($"Plaid event type: {eventType}");
                    
                    switch (eventType.ToUpperInvariant())
                    {
                        case "SUCCESS":
                            HandleSuccessCallback(root);
                            break;
                            
                        case "EXIT":
                            HandleExitCallback(root);
                            break;
                            
                        case "ERROR":
                            HandleErrorCallback(root);
                            break;
                            
                        case "EVENT": // Handle general Plaid events
                            HandleEventCallback(root);
                            break;
                            
                        default:
                            Debug.WriteLine($"Unknown Plaid event type: {eventType}");
                            break;
                    }
                }
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"Error parsing Plaid callback JSON: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling Plaid callback: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handles the SUCCESS event from Plaid Link
        /// </summary>
        private void HandleSuccessCallback(JsonElement root)
        {
            try
            {
                if (!root.TryGetProperty("metadata", out JsonElement metadata))
                {
                    Debug.WriteLine("Missing 'metadata' in SUCCESS event");
                    return;
                }
                
                // Extract public token
                string publicToken = "";
                if (metadata.TryGetProperty("public_token", out JsonElement tokenElement))
                {
                    publicToken = tokenElement.GetString() ?? string.Empty;
                }
                
                if (string.IsNullOrEmpty(publicToken))
                {
                    Debug.WriteLine("Missing public_token in SUCCESS event");
                    return;
                }
                
                // Extract institution name
                string institutionName = "Your Bank";
                if (metadata.TryGetProperty("institution", out JsonElement institution) &&
                    institution.TryGetProperty("name", out JsonElement nameElement))
                {
                    institutionName = nameElement.GetString() ?? institutionName;
                }
                
                Debug.WriteLine($"SUCCESS: public_token={publicToken}, institution={institutionName}");
                
                // Forward to the ViewModel
                ViewModel?.HandlePlaidSuccessAsync(publicToken, institutionName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling SUCCESS callback: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handles the EXIT event from Plaid Link
        /// </summary>
        private void HandleExitCallback(JsonElement root)
        {
            try
            {
                string error = string.Empty;
                string errorCode = string.Empty;
                
                // Check if metadata exists
                if (root.TryGetProperty("metadata", out JsonElement metadata))
                {
                    // Extract error information if available
                    if (metadata.TryGetProperty("error", out JsonElement errorElement))
                    {
                        error = errorElement.GetString() ?? string.Empty;
                    }
                    
                    if (metadata.TryGetProperty("error_code", out JsonElement errorCodeElement))
                    {
                        errorCode = errorCodeElement.GetString() ?? string.Empty;
                    }
                }
                
                Debug.WriteLine($"EXIT: error={error}, error_code={errorCode}");
                
                // Forward to the ViewModel
                ViewModel?.HandlePlaidExitAsync(error, errorCode);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling EXIT callback: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handles the ERROR event from Plaid Link
        /// </summary>
        private void HandleErrorCallback(JsonElement root)
        {
            try
            {
                string error = "Unknown error";
                string errorCode = "UNKNOWN";
                
                // Check if metadata exists
                if (root.TryGetProperty("metadata", out JsonElement metadata))
                {
                    // Extract error information
                    if (metadata.TryGetProperty("error", out JsonElement errorElement))
                    {
                        error = errorElement.GetString() ?? error;
                    }
                    
                    if (metadata.TryGetProperty("error_code", out JsonElement errorCodeElement))
                    {
                        errorCode = errorCodeElement.GetString() ?? errorCode;
                    }
                }
                
                Debug.WriteLine($"ERROR: error={error}, error_code={errorCode}");
                
                // Forward to the ViewModel's exit handler with error info
                ViewModel?.HandlePlaidExitAsync(error, errorCode);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling ERROR callback: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handles general events from Plaid Link
        /// </summary>
        private void HandleEventCallback(JsonElement root)
        {
            try
            {
                // Check if metadata exists
                if (!root.TryGetProperty("metadata", out JsonElement metadata))
                {
                    Debug.WriteLine("Missing 'metadata' in EVENT callback");
                    return;
                }
                
                // Extract event name
                string eventName = "unknown_event";
                if (metadata.TryGetProperty("event_name", out JsonElement eventNameElement))
                {
                    eventName = eventNameElement.GetString() ?? eventName;
                }
                
                // Log the event for debugging/monitoring
                Debug.WriteLine($"Plaid EVENT: {eventName}");
                
                // Handle specific events if needed
                switch (eventName)
                {
                    case "OPEN":
                        Debug.WriteLine("Plaid Link interface opened");
                        break;
                        
                    case "HANDOFF":
                        Debug.WriteLine("User handed off to the bank");
                        break;
                        
                    case "SELECT_INSTITUTION":
                        // Get institution name if available
                        string institutionName = "unknown bank";
                        if (metadata.TryGetProperty("metadata", out JsonElement metadataInner) &&
                            metadataInner.TryGetProperty("institution_name", out JsonElement institutionNameElement))
                        {
                            institutionName = institutionNameElement.GetString() ?? institutionName;
                        }
                        Debug.WriteLine($"User selected institution: {institutionName}");
                        break;
                        
                    case "SUBMIT_CREDENTIALS":
                        Debug.WriteLine("User submitted credentials");
                        break;
                        
                    case "SEARCH_INSTITUTION":
                        Debug.WriteLine("User searching for institution");
                        break;
                        
                    case "VIEW_DATA":
                        // User is reviewing data before agreeing to share
                        Debug.WriteLine("User reviewing data to share");
                        break;
                        
                    case "ERROR":
                        // This case is already handled by HandleErrorCallback
                        string errorType = "unknown_error";
                        if (metadata.TryGetProperty("metadata", out JsonElement errorMetadata) &&
                            errorMetadata.TryGetProperty("error_type", out JsonElement errorTypeElement))
                        {
                            errorType = errorTypeElement.GetString() ?? errorType;
                        }
                        Debug.WriteLine($"Error occurred: {errorType}");
                        break;
                        
                    default:
                        Debug.WriteLine($"Unhandled Plaid event: {eventName}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling EVENT callback: {ex.Message}");
            }
        }
    }

}